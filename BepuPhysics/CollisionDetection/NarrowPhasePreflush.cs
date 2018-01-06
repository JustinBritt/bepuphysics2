﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BepuPhysics.CollisionDetection
{
    internal enum PreflushJobType
    {
        /// <summary>
        /// Phase one job in the activator. JobIndex used to identify sub-job.
        /// </summary>
        ActivatorPhaseOne,
        /// <summary>
        /// Sorts the constraints of a single type across all workers. Used by deterministic preflushes to schedule adds.
        /// Accesses no buffer pools; memory is allocated and returned on main thread.
        /// </summary>
        SortContactConstraintType,
        /// <summary>
        /// Identifies a first guess at the constraint batch to which every new constraint should be added to. 
        /// Accesses no buffer pools; memory is allocated and returned on main thread.
        /// </summary>
        SpeculativeConstraintBatchSearch,

        //The deterministic constraint add is split into two dispatches. The to-add sort, speculative batch search, and body list add all happen first.
        //The actual addition process occurs afterward alongside the freshness checker.
        //It's slower than the nondeterministic path, but that's the bullet to bite.

        /// <summary>
        /// Adds constraints to the solver and constraint graph in an order determined by the previous sorts and with the help of the speculatively computed batch targets. Locally sequential.
        /// Accesses main thread buffer pool when type batches are created or resized.
        /// </summary>
        DeterministicConstraintAdd,
        /// <summary>
        /// Adds constraints to the solver and constraint graph in an order determined by the collision detection phase. If the collision detection phase is nondeterministic due to threading, then 
        /// this will result in nondeterministic adds to the solver.
        /// Accesses main thread buffer pool when type batches are created or resized.
        /// </summary>
        NondeterministicConstraintAdd,
        /// <summary>
        /// Phase two job in the activator. JobIndex used to identify sub-job.
        /// </summary>
        ActivatorPhaseTwo,
        /// <summary>
        /// Check the freshness bytes in a region to remove stale pairs.
        /// </summary>
        CheckFreshness,
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PreflushJob
    {
        [FieldOffset(0)]
        public PreflushJobType Type;
        /// <summary>
        /// Start region of a CheckFreshness or SpeculativeConstraintBatchSearch job.
        /// </summary>
        [FieldOffset(4)]
        public int Start;
        /// <summary>
        /// End region of a CheckFreshness or SpeculativeConstraintBatchSearch job.
        /// </summary>
        [FieldOffset(8)]
        public int End;
        /// <summary>
        /// Narrow phase constraint type index targeted by a SpeculativeConstraintBatchSearch or SortContactConstraintType.
        /// </summary>
        [FieldOffset(12)]
        public int TypeIndex;
        /// <summary>
        /// Index of the worker in which a range of constraints starts. 
        /// Used by SpeculativeConstraintBatchSearch.
        /// </summary>
        [FieldOffset(16)]
        public int WorkerIndex;
        /// <summary>
        /// Number of worker threads containing constraints to read in the SortContactConstraintType and NondeterministicConstraintAdd tasks.
        /// </summary>
        [FieldOffset(16)]
        public int WorkerCount;
        /// <summary>
        /// Index of the job. Used by ActivatorPhaseOne and ActivatorPhaseTwo tasks.
        /// </summary>
        [FieldOffset(4)]
        public int JobIndex;
    }

    public partial class NarrowPhase<TCallbacks>
    {
        internal struct SortConstraintTarget
        {
            public int WorkerIndex;
            public int ByteIndexInCache;
            //Note that we cache the handles as we do the initial pass to collect worker indices and byte indices.
            //While this does inflate the memory usage, note that 1024 pending constraints would only cost 16384 bytes. 
            //So extremely large simulations undergoing significant chaos may result in the sorting thread spilling out of L1, but virtually never L2.
            //This caching avoids the need to repeatedly pull in cache lines from the different worker sets. 
            //In chaotic large simulations, the poor cache line utilization (since we're only looking up 4-8 bytes) could result in spilling into L3.
            //As an added bonus, the access pattern during the initial pass is prefetchable, while a comparison-time lookup is not.
            public ulong Handles;
        }
        Buffer<QuickList<SortConstraintTarget, Buffer<SortConstraintTarget>>> sortedConstraints;

        int preflushJobIndex;
        QuickList<PreflushJob, Buffer<PreflushJob>> preflushJobs;
        Action<int> preflushWorkerLoop;
        SpinLock constraintAddLock = new SpinLock();
        void PreflushWorkerLoop(int workerIndex)
        {
            int jobIndex;
            while ((jobIndex = Interlocked.Increment(ref preflushJobIndex)) < preflushJobs.Count)
            {
                ExecutePreflushJob(workerIndex, ref preflushJobs[jobIndex]);
            }
        }

        struct PendingConstraintComparer : IComparerRef<SortConstraintTarget>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe int Compare(ref SortConstraintTarget a, ref SortConstraintTarget b)
            {
                return a.Handles.CompareTo(b.Handles);
            }
        }
        unsafe interface ISortingHandleCollector
        {
            ulong GetHandles(void* memory);
        }

        struct OneBodyHandleCollector : ISortingHandleCollector
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe ulong GetHandles(void* memory)
            {
                //Only one body; can't just return an 8 byte block of memory directly. Expand 4 bytes to 8.
                return *(uint*)memory;
            }
        }
        struct TwoBodyHandleCollector : ISortingHandleCollector
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe ulong GetHandles(void* memory)
            {
                //Two bodies- just reinterpret the existing memory.
                return *(ulong*)memory;
            }
        }


        unsafe void BuildSortingTargets<THandleCollector>(ref QuickList<SortConstraintTarget, Buffer<SortConstraintTarget>> list, int typeIndex, int workerCount) where THandleCollector : struct, ISortingHandleCollector
        {
            var handleCollector = default(THandleCollector);
            for (int i = 0; i < workerCount; ++i)
            {
                ref var workerList = ref overlapWorkers[i].PendingConstraints.pendingConstraintsByType[typeIndex];
                if (workerList.Count > 0)
                {
                    //This is doing redundant integer divides, but we lack type knowledge and it doesn't require any extra memory loading.
                    var entrySizeInBytes = workerList.ByteCount / workerList.Count;
                    int indexInBytes = 0;
                    for (int j = 0; j < workerList.Count; ++j)
                    {
                        ref var constraint = ref list.AllocateUnsafely();
                        constraint.WorkerIndex = i;
                        constraint.ByteIndexInCache = indexInBytes;
                        //Note two details:
                        //1) We rely on the layout of memory in the pending constraint add. If the body handles don't occupy the first bytes, this breaks.
                        //2) We rely on the order of body handles. The narrow phase should always guarantee a consistent order.
                        constraint.Handles = handleCollector.GetHandles(workerList.Buffer.Memory + indexInBytes);
                        indexInBytes += entrySizeInBytes;
                    }
                }
            }
        }

        unsafe void ExecutePreflushJob(int workerIndex, ref PreflushJob job)
        {
            switch (job.Type)
            {
                case PreflushJobType.CheckFreshness:
                    FreshnessChecker.CheckFreshnessInRegion(workerIndex, job.Start, job.End);
                    break;
                case PreflushJobType.SortContactConstraintType:
                    {
                        //The main thread has already allocated lists of appropriate capacities for all types that exist.
                        //We initialize and sort those lists on multiple threads.
                        ref var list = ref sortedConstraints[job.TypeIndex];
                        //One and two body constraints require separate initialization to work in a single pass with a minimum of last second branches.
                        //Use the type index to determine the body count.
                        //This follows the same convention as the GatherOldImpulses and ScatterNewImpulses of the PairCache.
                        //Constraints cover 16 possible cases:
                        //1-4 contacts: 0x3
                        //convex vs nonconvex: 0x4
                        //1 body versus 2 body: 0x8
                        //TODO: Very likely that we'll expand the nonconvex manifold maximum to 8 contacts, so this will need to be adjusted later.
                        if (job.TypeIndex >= 8)
                        {
                            BuildSortingTargets<TwoBodyHandleCollector>(ref list, job.TypeIndex, job.WorkerCount);
                        }
                        else
                        {
                            BuildSortingTargets<OneBodyHandleCollector>(ref list, job.TypeIndex, job.WorkerCount);
                        }

                        //Since duplicates are impossible (as that would imply the narrow phase generated two constraints for one pair), the non-threeway sort is used.
                        PendingConstraintComparer comparer;
                        QuickSort.Sort(ref list.Span[0], 0, list.Count - 1, ref comparer);
                    }
                    break;
                case PreflushJobType.SpeculativeConstraintBatchSearch:
                    {
                        overlapWorkers[job.WorkerIndex].PendingConstraints.SpeculativeConstraintBatchSearch(Solver, job.TypeIndex, job.Start, job.End);
                    }
                    break;
                case PreflushJobType.NondeterministicConstraintAdd:
                    {
                        for (int i = 0; i < job.WorkerCount; ++i)
                        {
                            overlapWorkers[i].PendingConstraints.FlushWithSpeculativeBatches(Simulation, ref PairCache);
                        }
                    }
                    break;
                case PreflushJobType.DeterministicConstraintAdd:
                    {
                        for (int typeIndex = 0; typeIndex < PairCache.CollisionConstraintTypeCount; ++typeIndex)
                        {
                            PendingConstraintAddCache.DeterministicallyAddType(typeIndex, overlapWorkers, ref sortedConstraints[typeIndex], Simulation, ref PairCache);
                        }
                    }
                    break;
                case PreflushJobType.ActivatorPhaseOne:
                    {
                        Simulation.Activator.ExecutePhaseOneJob(job.JobIndex);
                    }
                    break;
                case PreflushJobType.ActivatorPhaseTwo:
                    {
                        Simulation.Activator.ExecutePhaseTwoJob(job.JobIndex);
                    }
                    break;
            }
        }


        protected override void OnPreflush(IThreadDispatcher threadDispatcher, bool deterministic)
        {
            var threadCount = threadDispatcher == null ? 1 : threadDispatcher.ThreadCount;

            //Before we complete the addition of constraints, the pair cache's constraint handle->pair mapping must be made large enough to hold all existing constraints plus
            //any that we are about to add. There's no guarantee that we will use them (some earlier handles may be available), but we have no good way to know ahead of time.
            int newConstraintCount = 0;
            int setsToActivateCapacity = 0;
            for (int i = 0; i < threadCount; ++i)
            {
                newConstraintCount += overlapWorkers[i].PendingConstraints.CountConstraints();
                //This will tend to significantly overestimate the true set requirement, but that's not concerning- the maximum allocation won't be troublesome regardless.
                setsToActivateCapacity += overlapWorkers[i].PendingSetActivations.Count;
            }
            PairCache.EnsureConstraintToPairMappingCapacity(Solver, Solver.HandlePool.HighestPossiblyClaimedId + 1 + newConstraintCount);
            QuickList<int, Buffer<int>>.Create(Pool.SpecializeFor<int>(), setsToActivateCapacity, out var setsToActivate);
            var uniqueActivationsSet = new IndexSet(Pool, Simulation.Bodies.Sets.Length);
            for (int i = 0; i < threadCount; ++i)
            {
                Simulation.Activator.AccumulateUniqueIndices(ref overlapWorkers[i].PendingSetActivations, ref uniqueActivationsSet, ref setsToActivate);
            }
            uniqueActivationsSet.Dispose(Pool);
            for (int i = 0; i < threadCount; ++i)
            {
                overlapWorkers[i].PendingSetActivations.Dispose(overlapWorkers[i].Batcher.pool.SpecializeFor<int>());
            }
            (int activatorPhaseOneJobCount, int activatorPhaseTwoJobCount) = Simulation.Activator.PrepareJobs(ref setsToActivate, true, threadCount);
            if (threadCount > 1)
            {
                //Given the sizes involved, a fixed guess of 128 should be just fine for essentially any simulation. Overkill, but not in a concerning way.
                //Temporarily allocating 1KB of memory isn't a big deal, and we will only touch the necessary subset of it anyway.
                //(There are pathological cases where resizes are still possible, but the constraint remover handles them by not adding unsafely.)
                QuickList<PreflushJob, Buffer<PreflushJob>>.Create(Pool.SpecializeFor<PreflushJob>(), 128 + Math.Max(activatorPhaseOneJobCount, activatorPhaseTwoJobCount), out preflushJobs);

                //FIRST PHASE: 
                //1) If deterministic, sort each type batch.
                //2) Perform any activator phase one jobs (pair cache activations, update activated batch referenced handles, copy activated body regions).
                //3) Speculatively search for best-guess constraint batches for each new constraint in parallel.

                //Following the goals of the first phase, we have to responsibilities during job creation:
                //1) Scan through the workers and allocate space for the sorting handles to be added, if deterministic.
                //2) Speculative job creation walks through the types contained within each worker. Larger contiguous lists are subdivided into more than one job.
                //However, we never bother creating jobs which span boundaries between workers or types. While this can decrease the size of individual jobs below a useful level in some cases,
                //those are also the cases where the performance deficit doesn't matter. The simplicity of a single job operating only within a single list is worth more- plus, there
                //is a slight cache miss cost to jumping to a different area of memory in the middle of a job. Bundling that cost into the overhead of a new multithreaded task isn't a terrible thing.

                for (int i = 0; i < threadCount; ++i)
                {
                    overlapWorkers[i].PendingConstraints.AllocateForSpeculativeSearch();
                }
                var preflushJobPool = Pool.SpecializeFor<PreflushJob>();
                for (int i = 0; i < activatorPhaseOneJobCount; ++i)
                {
                    preflushJobs.Add(new PreflushJob { Type = PreflushJobType.ActivatorPhaseOne, JobIndex = i }, preflushJobPool);
                }
                //Note that we create the sort jobs ahead of batch finder. 
                //They tend to be individually much heftier than the constraint batch finder phase, and we'd like to be able to fill in the execution gaps.
                //TODO: It would be nice to have all the jobs semi-sorted by heftiness- that would just split the activator job creator loop. Only bother if profiling suggests it.
                if (deterministic)
                {
                    Pool.SpecializeFor<QuickList<SortConstraintTarget, Buffer<SortConstraintTarget>>>().Take(PairCache.CollisionConstraintTypeCount, out sortedConstraints);
                    sortedConstraints.Clear(0, PairCache.CollisionConstraintTypeCount);
                    for (int typeIndex = 0; typeIndex < PairCache.CollisionConstraintTypeCount; ++typeIndex)
                    {
                        int countInType = 0;
                        for (int workerIndex = 0; workerIndex < threadCount; ++workerIndex)
                        {
                            countInType += overlapWorkers[workerIndex].PendingConstraints.pendingConstraintsByType[typeIndex].Count;
                        }
                        if (countInType > 0)
                        {
                            //Note that we don't actually add any constraint targets here- we let the actual worker threads do that. No reason not to, and it extracts a tiny bit of extra parallelism.
                            QuickList<SortConstraintTarget, Buffer<SortConstraintTarget>>.Create(Pool.SpecializeFor<SortConstraintTarget>(), countInType, out sortedConstraints[typeIndex]);
                            preflushJobs.Add(new PreflushJob { Type = PreflushJobType.SortContactConstraintType, TypeIndex = typeIndex, WorkerCount = threadCount }, preflushJobPool);
                        }
                    }
                }
                const int maximumConstraintsPerJob = 16; //TODO: Empirical tuning.

                for (int typeIndex = 0; typeIndex < PairCache.CollisionConstraintTypeCount; ++typeIndex)
                {
                    for (int workerIndex = 0; workerIndex < threadCount; ++workerIndex)
                    {
                        var count = overlapWorkers[workerIndex].PendingConstraints.pendingConstraintsByType[typeIndex].Count;
                        if (count > 0)
                        {
                            var jobCount = 1 + count / maximumConstraintsPerJob;
                            var jobSize = count / jobCount;
                            var remainder = count - jobCount * jobSize;
                            int previousEnd = 0;
                            for (int i = 0; i < jobCount; ++i)
                            {
                                var jobStart = previousEnd;
                                var constraintsInJob = jobSize;
                                if (i < remainder)
                                    ++constraintsInJob;
                                previousEnd += constraintsInJob;
                                preflushJobs.Add(new PreflushJob
                                {
                                    Type = PreflushJobType.SpeculativeConstraintBatchSearch,
                                    Start = jobStart,
                                    End = previousEnd,
                                    TypeIndex = typeIndex,
                                    WorkerIndex = workerIndex
                                }, preflushJobPool);
                            }
                            Debug.Assert(previousEnd == count);
                        }
                    }
                }
                //var start = Stopwatch.GetTimestamp();
                preflushJobIndex = -1;
                threadDispatcher.DispatchWorkers(preflushWorkerLoop);
                //for (int i = 0; i < preflushJobs.Count; ++i)
                //{
                //    ExecutePreflushJob(0, ref preflushJobs[i]);
                //}
                //var end = Stopwatch.GetTimestamp();
                //Console.WriteLine($"Preflush phase 1 time (us): {1e6 * (end - start) / Stopwatch.Frequency}");

                //SECOND PHASE:
                //1) Locally sequential constraint adds. This is the beefiest single task, and it runs on one thread. It can be deterministic or nondeterministic.
                //2) Activator phase two (broadphase update, constraint region copies).
                //3) Freshness checker. Lots of smaller jobs that can hopefully fill the gap while the constraint adds finish. The wider the CPU, the less this will be possible.

                preflushJobs.Clear(); //Note job clear. We're setting up new jobs.
                if (deterministic)
                {
                    preflushJobs.Add(new PreflushJob { Type = PreflushJobType.DeterministicConstraintAdd }, preflushJobPool);
                }
                else
                {
                    preflushJobs.Add(new PreflushJob { Type = PreflushJobType.NondeterministicConstraintAdd, WorkerCount = threadCount }, preflushJobPool);
                }
                for (int i = 0; i < activatorPhaseTwoJobCount; ++i)
                {
                    preflushJobs.Add(new PreflushJob { Type = PreflushJobType.ActivatorPhaseTwo, JobIndex = i }, preflushJobPool);
                }
                FreshnessChecker.CreateJobs(threadCount, ref preflushJobs, Pool);

                //start = Stopwatch.GetTimestamp();
                preflushJobIndex = -1;
                threadDispatcher.DispatchWorkers(preflushWorkerLoop);
                //for (int i = 0; i < preflushJobs.Count; ++i)
                //{
                //    ExecutePreflushJob(0, ref preflushJobs[i]);
                //}
                //end = Stopwatch.GetTimestamp();
                //Console.WriteLine($"Preflush phase 2 time (us): {1e6 * (end - start) / Stopwatch.Frequency}");

                for (int i = 0; i < threadCount; ++i)
                {
                    overlapWorkers[i].PendingConstraints.DisposeSpeculativeSearch();
                }
                if (deterministic)
                {
                    var targetPool = Pool.SpecializeFor<SortConstraintTarget>();
                    for (int i = 0; i < PairCache.CollisionConstraintTypeCount; ++i)
                    {
                        ref var typeList = ref sortedConstraints[i];
                        if (typeList.Span.Allocated)
                            typeList.Dispose(targetPool);
                    }
                    Pool.SpecializeFor<QuickList<SortConstraintTarget, Buffer<SortConstraintTarget>>>().Return(ref sortedConstraints);
                }
                preflushJobs.Dispose(preflushJobPool);
            }
            else
            {
                //Single threaded. Quite a bit simpler!
                //Three tasks: activate, freshness checker, and add all pending constraints.
                if (activatorPhaseOneJobCount > 0)
                {
                    Console.Write($"Activation! {activatorPhaseOneJobCount} phase one, {activatorPhaseTwoJobCount} phase two. Sets: ");
                    for (int i = 0; i < setsToActivate.Count; ++i)
                    {
                        Console.Write($"{setsToActivate[i]}, ");
                    }
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine("No activations.");
                }
                //Note that phase one changes the PairCache.Mapping.Count; the count must be cached so that the freshness checker doesn't bother analyzing the newly activated pairs.
                var originalMappingCount = PairCache.Mapping.Count;
                for (int i = 0; i < activatorPhaseOneJobCount; ++i)
                    Simulation.Activator.ExecutePhaseOneJob(i);
                //Note that phase one of activation must occur before the constraint flush. Phase one registers the newly activated constraints in constraint batches.
                //This this was not done, pending adds might end up in the same batches as newly activated constraints that share bodies.
                overlapWorkers[0].PendingConstraints.FlushSequentially(Simulation, PairCache);
                FreshnessChecker.CheckFreshnessInRegion(0, 0, originalMappingCount);
                for (int i = 0; i < activatorPhaseTwoJobCount; ++i)
                    Simulation.Activator.ExecutePhaseTwoJob(i);
            }
            for (int i = 0; i < threadCount; ++i)
            {
                overlapWorkers[i].PendingConstraints.Dispose();
            }
            if (setsToActivate.Count > 0)
            {
                Simulation.Activator.DisposeForCompletedActivations(ref setsToActivate);
            }
            setsToActivate.Dispose(Pool.SpecializeFor<int>());

        }
    }
}
