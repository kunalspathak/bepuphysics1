# What's substepping?
Substepping integrates body velocities and positions and solves constraints more than once per call to `Simulation.Timestep`. For some simulations with complex constraint configurations, high stiffness, or high mass ratios, substepping is the fastest way to find a stable solution.

You can configure a simulation to use substepping by passing a `SolveDescription` to `Simulation.Create` that has more than one substep. For example, to create a simulation that uses 8 substeps and 1 velocity iteration per substep:
```cs
var simulation = Simulation.Create(BufferPool, new NarrowPhaseCallbacks(), new PoseIntegratorCallbacks(), new SolveDescription(velocityIterationCount: 1, substepCount: 8));
```

# Why use it?

# Limitations

# How substepping fits into a timestep
Each call to `Simulation.Timestep(dt, ...)` simulates one frame with duration equal to `dt`. In the [`DefaultTimestepper`](../BepuPhysics/DefaultTimestepper.cs) (which, as the name implies, is the `ITimestepper` implementation used if no other is specified) executes a frame like so:
```cs
simulation.Sleep();
simulation.PredictBoundingBoxes(dt, threadDispatcher);
simulation.CollisionDetection(dt, threadDispatcher);
simulation.Solve(dt, threadDispatcher);
simulation.IncrementallyOptimizeDataStructures(threadDispatcher);
```
There's only one execution of collision each stage per call to `Timestep`, each responsible for covering the specified `dt`.

When configured to use more than one substep, `Simulation.Solve` will integrate bodies and solve constraints as if `Simulation.Timestep` was called `Simulation.Solver.SubstepCount` times, each time with a duration equal to `dt / Simulation.Solver.SubstepCount`.

The difference between using substepping and explicitly calling `Timestep` more frequently is that none of the other stages run during substeps. For example, contact constraints are incrementally updated in an approximate way, but full collision detection is not run. This allows substeps to be much faster than full timesteps.

# Velocity iteration scheduling
While the simplest approach is to use the same number of velocity iterations for all substeps, they are allowed to vary. You can provide a `VelocityIterationScheduler` callback in the `SolveDescription` to define how many velocity iterations each substep should take. There's also a helper that takes a span of integers defining the velocity iteration counts to use for each substep.
```cs
var simulation = Simulation.Create(BufferPool, new NarrowPhaseCallbacks(), new PoseIntegratorCallbacks(), new SolveDescription(new[] {2, 1, 1}));
```
The above snippet would use 3 substeps with 2 velocity iterations on the first substep, then 1 velocity iteration on the second and third substeps.

This can be helpful when trying to find the absolute cheapest configuration that is still stable for a particular simulation. For example, a simulation with 1 velocity iteration per substep could be observed to be stable at 4 substeps but not at 3 substeps, and adding an extra velocity iteration to the first substep could make 3 substeps stable at a lower cost than 4 substeps. In other words, variable velocity iterations let you manage the simulation budget in a finer grained way.

There are some cases where intentionally frontloading iterations could be useful as well. If you know the simulation has changed significantly since the last timestep- perhaps you've moved a bunch of bodies around such that the previous frame's guess at a constraint solution will be very wrong- then running a few more velocity iterations on the first timestep can avoid accumulating error.

# Dynamic changes to substep and velocity iteration counts
The solver is sensitive to the effective timestep duration. It caches a best guess of the constraint solution which is sensitive to the amount of time passing between solves, so large changes can ruin the guess and harm stability. It's best to use the same timestep duration (`dt` passed into `Simulation.Timestep`) and the same number of substeps if possible, since the effective timestep duration seen by the solver is `dt / substepCount`.

Incremental changes to the `dt` value can still work if they're reasonably small. 'Reasonably small' here has no precise definition; it will vary depending on the stability requirements of the simulation and how much error the application can tolerate. Changing `dt` by 1% per frame is probably okay for most simulations. Changing it by 50% per frame probably isn't.

Changing the number of substeps is harder to do in a continuous way. Going from 4 to 3 substeps with a 60hz outer timestep rate means going from 240hz to 180hz effective solve rate instantly. That could be enough to cause problems for some simulations. You'd likely want to increase the number of velocity iterations in the first substep of the next frame to try to correct some of the induced error.

It's also possible to update the cached guess in response to a timestep change using `Solver.ScaleAccumulatedImpulses`. The scale should be `newEffectiveTimestepDuration / oldEffectiveTimestepDuration`, or in other words `(newDt / newSubstepCount) / (oldDt / oldSubstepCount)`. This operation is not very cheap: it touches all accumulated impulses memory.

Changing the number of *velocity iterations* from frame to frame is safe. The more velocity iterations there are, the closer the solution will converge to an optimum during the substep.

# Callbacks

The solver exposes events that fire at the beginning and end of each substep: `SubstepStarted` and `SubstepEnded`. These events are called from worker thread 0 in the solver's thread dispatch; the dispatch does not end in between substeps to keep overhead low. 

(Note that attempting to dispatch multithreaded work from the same `IThreadDispatcher` instance that dispatched the solver's workers requires that the `IThreadDispatcher` implementation is reentrant. The demos `SimpleThreadDispatcher` is not.)
