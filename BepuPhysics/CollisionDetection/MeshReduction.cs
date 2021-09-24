﻿using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection
{
    public unsafe struct MeshReduction : ICollisionTestContinuation
    {
        /// <summary>
        /// Flag used to mark a contact as being generated by the face of a triangle in its feature id.
        /// </summary>
        public const int FaceCollisionFlag = 32768;
        /// <summary>
        /// Minimum dot product between a triangle face and the contact normal for a collision to be considered a triangle face contact.
        /// </summary>
        public const float MinimumDotForFaceCollision = 0.999999f;
        public Buffer<Triangle> Triangles;
        //MeshReduction relies on all of a mesh's triangles being in slot B, as they appear in the mesh collision tasks.
        //However, the original user may have provided this pair in unknown order and triggered a flip. We'll compensate for that when examining contact positions.
        public bool RequiresFlip;
        //The triangles array is in the mesh's local space. In order to test any contacts against them, we need to be able to transform contacts.
        public Quaternion MeshOrientation;
        public BoundingBox QueryBounds;
        //This uses all of the nonconvex reduction's logic, so we just nest it.
        public NonconvexReduction Inner;

        public void* Mesh; //TODO: This is not flexible with respect to different mesh types. Not a problem right now, but it will be in the future.

        public void Create(int childManifoldCount, BufferPool pool)
        {
            Inner.Create(childManifoldCount, pool);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void OnChildCompleted<TCallbacks>(ref PairContinuation report, ref ConvexContactManifold manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompleted(ref report, ref manifold, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChildCompletedEmpty<TCallbacks>(ref PairContinuation report, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompletedEmpty(ref report, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ComputeMeshSpaceContact(ref ConvexContactManifold manifold, in Matrix3x3 inverseMeshOrientation, bool requiresFlip, out Vector3 meshSpaceContact, out Vector3 meshSpaceNormal)
        {
            //Select the deepest contact out of the manifold. Our goal is to find a contact on the representative feature of the source triangle.
            //Recall that triangle collision tests will generate speculative contacts elsewhere on the triangle, both on the face and potentially on edges
            //other than the deepest edge.
            //The *normal*, however, is most directly associated with the deepest contact. The fact that the normal is 'infringing' on some other edge doesn't really matter.
            //(Why doesn't it matter? MeshReduction operates on single convex-mesh pairs at a time. The *convex* shape cannot generate genuinely infringing contacts on two sides of a triangle at once.
            //The opposing edge's contact will actually point *away* from that edge toward the interior of the source triangle. For the same reason that we never block face contacts, it doesn't make sense to 
            //block based on those incidental contacts.)
            //This is equivalent to using the normal to determine the manifold voronoi region, except the contact position lets us deal with more arbitrary content.
            var deepestIndex = 0;
            var deepestDepth = manifold.Contact0.Depth;
            for (int j = 1; j < manifold.Count; ++j)
            {
                var depth = Unsafe.Add(ref manifold.Contact0, j).Depth;
                if (deepestDepth < depth)
                {
                    deepestDepth = depth;
                    deepestIndex = j;
                }
            }
            //First, if the manifold considers the mesh and its triangles to be shape B, then we need to flip it.
            if (requiresFlip)
            {
                //If the manifold considers the mesh and its triangles to be shape B, it needs to be flipped before being transformed.
                Matrix3x3.Transform(Unsafe.Add(ref manifold.Contact0, deepestIndex).Offset - manifold.OffsetB, inverseMeshOrientation, out meshSpaceContact);
                Matrix3x3.Transform(-manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
            else
            {
                //No flip required.
                Matrix3x3.Transform(Unsafe.Add(ref manifold.Contact0, deepestIndex).Offset, inverseMeshOrientation, out meshSpaceContact);
                Matrix3x3.Transform(manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
        }

        struct TestTriangle
        {
            //The test triangle contains SOA-ified layouts for quicker per contact testing.
            public Vector4 AnchorX;
            public Vector4 AnchorY;
            public Vector4 AnchorZ;
            public Vector4 NX;
            public Vector4 NY;
            public Vector4 NZ;
            public float DistanceThreshold;
            public int ChildIndex;
            /// <summary>
            /// True if the manifold associated with this triangle has been blocked due to its detected infringement on another triangle, false otherwise.
            /// </summary>
            public bool Blocked;
            /// <summary>
            /// True if the triangle did not act as a blocker for any other manifold and so can be removed if it is blocked, false otherwise.
            /// </summary>
            public bool ForceDeletionOnBlock;
            /// <summary>
            /// Normal of a triangle detected as being infringed by the manifold associated with this triangle in mesh space.
            /// </summary>
            public Vector3 CorrectedNormal;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TestTriangle(in Triangle triangle, int sourceChildIndex)
            {
                var ab = triangle.B - triangle.A;
                var bc = triangle.C - triangle.B;
                var ca = triangle.A - triangle.C;
                //TODO: This threshold might result in bumps when dealing with small triangles. May want to include a different source of scale information, like from the original convex test.
                DistanceThreshold = 1e-3f * (float)Math.Sqrt(MathHelper.Max(triangle.A.LengthSquared() * 1e-4f, MathHelper.Max(ab.LengthSquared(), ca.LengthSquared())));
                var n = Vector3.Cross(ab, ca);
                //Edge normals point outward.
                var edgeNormalAB = Vector3.Cross(n, ab);
                var edgeNormalBC = Vector3.Cross(n, bc);
                var edgeNormalCA = Vector3.Cross(n, ca);

                NX = new Vector4(n.X, edgeNormalAB.X, edgeNormalBC.X, edgeNormalCA.X);
                NY = new Vector4(n.Y, edgeNormalAB.Y, edgeNormalBC.Y, edgeNormalCA.Y);
                NZ = new Vector4(n.Z, edgeNormalAB.Z, edgeNormalBC.Z, edgeNormalCA.Z);
                var normalLengthSquared = NX * NX + NY * NY + NZ * NZ;
                var inverseLength = Vector4.One / Vector4.SquareRoot(normalLengthSquared);
                NX *= inverseLength;
                NY *= inverseLength;
                NZ *= inverseLength;
                AnchorX = new Vector4(triangle.A.X, triangle.A.X, triangle.B.X, triangle.C.X);
                AnchorY = new Vector4(triangle.A.Y, triangle.A.Y, triangle.B.Y, triangle.C.Y);
                AnchorZ = new Vector4(triangle.A.Z, triangle.A.Z, triangle.B.Z, triangle.C.Z);

                ChildIndex = sourceChildIndex;
                Blocked = false;
                ForceDeletionOnBlock = true;
                CorrectedNormal = default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool ShouldBlockNormal(in TestTriangle triangle, in Vector3 meshSpaceContact, in Vector3 meshSpaceNormal)
        {
            //While we don't have a decent way to do truly scaling SIMD operations within the context of a single manifold vs triangle test, we can at least use 4-wide operations
            //to accelerate each individual contact test. 
            // distanceFromPlane = (Position - a) * N / ||N||
            // distanceFromPlane^2 = ((Position - a) * N)^2 / (N * N)
            // distanceAlongEdgeNormal^2 = ((Position - edgeStart) * edgeN)^2 / ||edgeN||^2

            //There are four lanes, one for each plane of consideration:
            //X: Plane normal
            //Y: AB edge normal
            //Z: BC edge normal
            //W: CA edge normal
            //They're all the same operation, so we can do them 4-wide. That's better than doing a bunch of individual horizontal dot products.
            var px = new Vector4(meshSpaceContact.X);
            var py = new Vector4(meshSpaceContact.Y);
            var pz = new Vector4(meshSpaceContact.Z);
            var offsetX = px - triangle.AnchorX;
            var offsetY = py - triangle.AnchorY;
            var offsetZ = pz - triangle.AnchorZ;
            var distanceAlongNormal = offsetX * triangle.NX + offsetY * triangle.NY + offsetZ * triangle.NZ;
            //Note that very very thin triangles can result in questionable acceptance due to not checking for true distance- 
            //a position might be way outside a vertex, but still within edge plane thresholds. We're assuming that the impact of this problem will be minimal.
            if (distanceAlongNormal.X <= triangle.DistanceThreshold &&
                distanceAlongNormal.Y <= triangle.DistanceThreshold &&
                distanceAlongNormal.Z <= triangle.DistanceThreshold &&
                distanceAlongNormal.W <= triangle.DistanceThreshold)
            {
                //The contact is near the triangle. Is the normal infringing on the triangle's face region?
                //This occurs when:
                //1) The contact is near an edge, and the normal points inward along the edge normal.
                //2) The contact is on the inside of the triangle.
                //Note that we are stricter about being on the edge than we were about being nearby.
                //That's because infringement checks require a normal infringement along every edge that the contact is on;
                //being too aggressive about edge classification would cause infringements to sometimes be ignored.
                var negativeThreshold = triangle.DistanceThreshold * -1e-2f;
                var onAB = distanceAlongNormal.Y >= negativeThreshold;
                var onBC = distanceAlongNormal.Z >= negativeThreshold;
                var onCA = distanceAlongNormal.W >= negativeThreshold;
                if (!onAB && !onBC && !onCA)
                {
                    //The contact is within the triangle. 
                    //If this contact resulted in a correction, we can skip the remaining contacts in this manifold.
                    return true;
                }
                else
                {
                    //The contact is on the border of the triangle. Is the normal pointing outward on any edge that the contact is on?
                    //Remember, the contact has been pushed into mesh space. The position is on the surface of the triangle, and the normal points from convex to mesh.
                    //The edge plane normals point outward from the triangle, so if the contact normal is detected as pointing along the edge plane normal,
                    //then it is infringing.
                    var normalDot = triangle.NX * meshSpaceNormal.X + triangle.NY * meshSpaceNormal.Y + triangle.NZ * meshSpaceNormal.Z;
                    const float infringementEpsilon = 1e-6f;
                    //In order to block a contact, it must be infringing on every edge that it is on top of.
                    //In other words, when a contact is on a vertex, it's not good enough to infringe only one of the edges; in that case, the contact normal isn't 
                    //actually infringing on the triangle face.
                    //Further, note that we require nonzero positive infringement; otherwise, we'd end up blocking the contacts of a flat neighbor.
                    //But we are a little more aggressive about blocking the *second* edge infringement- if it's merely parallel, we count it as infringing.
                    //Otherwise you could get into situations where a contact on the vertex of a bunch of different triangles isn't blocked by any of them because
                    //the normal is aligned with an edge.
                    if ((onAB && normalDot.Y > infringementEpsilon) || (onBC && normalDot.Z > infringementEpsilon) || (onCA && normalDot.W > infringementEpsilon))
                    {
                        const float secondaryInfringementEpsilon = -1e-2f;
                        //At least one edge is infringed. Are all contact-touched edges at least nearly infringed?
                        if ((!onAB || normalDot.Y > secondaryInfringementEpsilon) && (!onBC || normalDot.Z > secondaryInfringementEpsilon) && (!onCA || normalDot.W > secondaryInfringementEpsilon))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        //static void RemoveContacts()
        //{
        //    //Note that the removal had to be deferred until after blocking analysis.
        //    //This manifold will not be considered for the remainder of this loop, so modifying it is fine.
        //    for (int j = sourceChild.Manifold.Count - 1; j >= 0; --j)
        //    {
        //        //If a contact is outside of the mesh space bounding box that found the triangles to test, then two things are true:
        //        //1) The contact is almost certainly not productive; the bounding box included a frame of integrated motion and this contact was outside of it.
        //        //2) The contact may have been created with a triangle whose neighbor was not in the query bounds, and so the neighbor won't contribute any blocking.
        //        //The result is that such contacts have a tendency to cause ghost collisions. We'd rather not force the use of very small speculative margins,
        //        //so instead we explicitly kill off contacts which are outside the queried bounds.
        //        ref var contactToCheck = ref meshSpaceContacts[j];
        //        if (Vector3.Min(contactToCheck, queryBounds.Min) != queryBounds.Min ||
        //            Vector3.Max(contactToCheck, queryBounds.Max) != queryBounds.Max)
        //        {
        //            ConvexContactManifold.FastRemoveAt(ref sourceChild.Manifold, j);
        //        }
        //    }
        //}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void TryApplyBlockToTriangle(ref TestTriangle triangle, Buffer<NonconvexReductionChild> children, in Matrix3x3 meshOrientation, bool requiresFlip)
        {
            if (triangle.Blocked)
            {
                ref var manifold = ref children[triangle.ChildIndex].Manifold;
                if (triangle.ForceDeletionOnBlock)
                {
                    //The manifold was infringing, and no other manifold infringed upon it. Can safely just ignore the manifold completely.
                    manifold.Count = 0;
                }
                else
                {
                    var manifoldHasPositiveDepth = false;
                    for (int j = 0; j < manifold.Count; ++j)
                    {
                        if (Unsafe.Add(ref manifold.Contact0, j).Depth > 0)
                        {
                            manifoldHasPositiveDepth = true;
                            break;
                        }
                    }
                    if (manifoldHasPositiveDepth)
                    {
                        //The manifold was infringing, but another manifold was infringing upon it. We can't safely delete such a manifold since it's likely a mutually infringing 
                        //case- consider what happens when an objects wedges itself into an edge between two triangles.                            
                        Matrix3x3.Transform(requiresFlip ? triangle.CorrectedNormal : -triangle.CorrectedNormal, meshOrientation, out manifold.Normal);
                        //Note that we do not modify the depth.
                        //The only times this situation should occur is when either 1) an object has somehow wedged between adjacent triangles such that the detected
                        //depths are *less* than the triangle face depths, or 2) a source triangle generated an internal contact, and the face depth is guaranteed to be less.
                        //So, using those depths is guaranteed not to introduce excessive energy.
                    }
                    else
                    {
                        //The manifold has zero or negative depth; it's clearly not a case where a shape is wedged between triangles. Just get rid of it.
                        manifold.Count = 0;
                    }
                }
            }
        }

        struct ChildEnumerator : IBreakableForEach<int>
        {
            public QuickList<int> List;
            public BufferPool Pool;
            public bool LoopBody(int i)
            {
                List.Allocate(Pool) = i;
                return true;
            }
        }

        public unsafe static void ReduceManifolds(ref Buffer<Triangle> continuationTriangles, ref Buffer<NonconvexReductionChild> continuationChildren, int start, int count,
           bool requiresFlip, in BoundingBox queryBounds, in Matrix3x3 meshOrientation, in Matrix3x3 meshInverseOrientation, Mesh* mesh, BufferPool pool)
        {
            //Before handing responsibility off to the nonconvex reduction, make sure that no contacts create nasty 'bumps' at the border of triangles.
            //Bumps can occur when an isolated triangle test detects a contact pointing outward, like when a box hits the side. This is fine when the triangle truly is isolated,
            //but if there's a neighboring triangle that's snugly connected, the user probably wants the two triangles to behave as a single coherent surface. So, contacts
            //with normals which wouldn't exist in the ideal 'continuous' form of the surface need to be corrected.

            //A contact is a candidate for correction if it meets three conditions:
            //1) The contact was not generated by a face collision, and
            //2) The contact position is touching another triangle, and
            //3) The contact normal is infringing on the neighbor's face voronoi region.

            //Contacts generated by face collisions are always immediately accepted without modification. 
            //The only time they can cause infringement is when the surface is concave, and in that case, the face normal is correct and will not cause any inappropriate bumps.

            //A contact that isn't touching a triangle can't infringe upon it.
            //Note that triangle-involved manifolds always generate contacts such that the position is on the triangle to make this test meaningful.
            //(That's why the MeshReduction has to be aware of whether the manifolds have been flipped- so that we know we're working with consistent slots.)

            //Contacts generated by face collisions are marked with a special feature id flag. If it is present, we can skip the contact. The collision tester also provided unique feature ids
            //beyond that flag, so we can strip the flag now. (We effectively just hijacked the feature id to store some temporary metadata.)

            //If you don't want to run mesh reduction at all for sufficiently complex pairs, you could simply early out here like so:
            //if (count > 1024)
            //    return;

            //Narrow the region of interest.
            continuationTriangles.Slice(start, count, out var triangles);
            continuationChildren.Slice(start, count, out var children);
            const int bruteForceThreshold = 16;
            //Console.WriteLine($"count: {count}");
            if (count < bruteForceThreshold)
            {
                var memory = stackalloc TestTriangle[count];
                var activeTriangles = new Buffer<TestTriangle>(memory, count);
                for (int i = 0; i < count; ++i)
                {
                    activeTriangles[i] = new TestTriangle(triangles[i], i);
                }

                for (int i = 0; i < count; ++i)
                {
                    ref var sourceTriangle = ref activeTriangles[i];
                    ref var sourceChild = ref children[sourceTriangle.ChildIndex];
                    //Can't correct contacts that were created by face collisions.
                    var faceFlagUnset = (sourceChild.Manifold.Contact0.FeatureId & FaceCollisionFlag) == 0;
                    if (faceFlagUnset && sourceChild.Manifold.Count > 0)
                    {
                        ComputeMeshSpaceContact(ref sourceChild.Manifold, meshInverseOrientation, requiresFlip, out var meshSpaceContact, out var meshSpaceNormal);

                        for (int j = 0; j < count; ++j)
                        {
                            ref var targetTriangle = ref activeTriangles[j];
                            if (ShouldBlockNormal(targetTriangle, meshSpaceContact, meshSpaceNormal))
                            {
                                sourceTriangle.Blocked = true;
                                sourceTriangle.CorrectedNormal = new Vector3(targetTriangle.NX.X, targetTriangle.NY.X, targetTriangle.NZ.X);
                                //Even if the target manifold gets blocked, it should not necessarily be deleted. We made use of it as a blocker.
                                targetTriangle.ForceDeletionOnBlock = false;
                                break;
                            }
                        }

                        //RemoveContacts();

                        //var testDot = Vector3.Dot(meshSpaceNormal, new Vector3(sourceTriangle.NX.X, sourceTriangle.NY.X, sourceTriangle.NZ.X));
                        //if (MathF.Abs(testDot) < 0.3f && !sourceTriangle.Blocked && sourceChild.Manifold.Count > 0)
                        //{
                        //    Console.WriteLine($"Iffy dot: {testDot} NOT BLOCKED");
                        //}
                    }
                    else if (!faceFlagUnset)
                    {
                        //Clear the face flags. This isn't *required* since they're coherent enough anyway and the accumulated impulse redistributor is a decent fallback,
                        //but it costs basically nothing to do this.
                        for (int k = 0; k < sourceChild.Manifold.Count; ++k)
                        {
                            Unsafe.Add(ref sourceChild.Manifold.Contact0, k).FeatureId &= ~FaceCollisionFlag;
                        }
                    }
                }
                for (int i = 0; i < count; ++i)
                {
                    TryApplyBlockToTriangle(ref activeTriangles[i], children, meshOrientation, requiresFlip);
                }
            }
            else
            {

                ChildEnumerator enumerator;
                //Queries can sometimes find triangles that are just barely outside the original child set. It's rare, but there's no reason to force a resize if it does happen.
                //Allocate a bit more to make resizes almost-but-not-quite impossible.
                var allocationSize = count * 2;
                enumerator.Pool = pool;
                enumerator.List = new QuickList<int>(allocationSize, pool);
                QuickDictionary<int, TestTriangle, PrimitiveComparer<int>> testTriangles = new(allocationSize, pool);
                //For numerical reasons, expand each contact by an epsilon to capture relevant triangles.
                var span = queryBounds.Max - queryBounds.Min;
                var maxSpan = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
                var contactExpansion = new Vector3(maxSpan * 1e-4f);

                //We're likely to encounter all the triangles that we collected, so go ahead and create their entries.
                //Note that this is also used to keep the indices lined up for the TryApplyBlockToTriangle loop.
                for (int i = 0; i < count; ++i)
                {
                    testTriangles.AddUnsafely(children[i].ChildIndexB, new TestTriangle(triangles[i], i));
                }
                for (int i = 0; i < count; ++i)
                {
                    ref var sourceTriangle = ref testTriangles.Values[i];
                    ref var sourceChild = ref children[sourceTriangle.ChildIndex];
                    //Can't correct contacts that were created by face collisions.
                    var faceFlagUnset = (sourceChild.Manifold.Contact0.FeatureId & FaceCollisionFlag) == 0;
                    if (faceFlagUnset && sourceChild.Manifold.Count > 0)
                    {
                        ComputeMeshSpaceContact(ref sourceChild.Manifold, meshInverseOrientation, requiresFlip, out var meshSpaceContact, out var meshSpaceNormal);
                        var contactQueryMin = meshSpaceContact - contactExpansion;
                        var contactQueryMax = meshSpaceContact + contactExpansion;
                        enumerator.List.Count = 0;
                        mesh->Tree.GetOverlaps(contactQueryMin, contactQueryMax, ref enumerator);
                        //Note that the test triangles detected by querying may exceed the count in extremely rare cases, so it's not safe to use AllocateUnsafely without some extra work.
                        //Resizing invalidates table indices, so do any that ahead of time.
                        testTriangles.EnsureCapacity(testTriangles.Count + enumerator.List.Count, pool);
                        for (int j = 0; j < enumerator.List.Count; ++j)
                        {
                            var triangleIndexInMesh = enumerator.List[j];
                            if (!testTriangles.FindOrAllocateSlotUnsafely(triangleIndexInMesh, out var triangleIndex))
                            {
                                //Note that this does not try to do a direct lookup of the triangle data in the Mesh's triangles buffer!
                                //That's invalid for two reasons:
                                //1) in the long term, the mesh type will be abstracted away, and we might be dealing with a type that doesn't have a Triangles buffer at all.
                                //2) the Mesh applies a scale to the stored triangles! That's why we have the continuation triangles explicitly stored rather than just looking them all up in the mesh-
                                //the convex-triangle tests that preceded this reduction had to have somewhere they could load the 'baked' triangle data from.
                                mesh->GetLocalChild(triangleIndexInMesh, out var triangle);
                                testTriangles.Values[triangleIndex] = new TestTriangle(triangle, triangleIndex);
                            }
                            ref var targetTriangle = ref testTriangles.Values[triangleIndex];

                            if (ShouldBlockNormal(targetTriangle, meshSpaceContact, meshSpaceNormal))
                            {
                                sourceTriangle.Blocked = true;
                                sourceTriangle.CorrectedNormal = new Vector3(targetTriangle.NX.X, targetTriangle.NY.X, targetTriangle.NZ.X);
                                //Even if the target manifold gets blocked, it should not necessarily be deleted. We made use of it as a blocker.
                                targetTriangle.ForceDeletionOnBlock = false;
                                break;
                            }
                        }
                        //var testDot = Vector3.Dot(meshSpaceNormal, new Vector3(sourceTriangle.NX.X, sourceTriangle.NY.X, sourceTriangle.NZ.X));
                        //if (MathF.Abs(testDot) < 0.3f && !sourceTriangle.Blocked && sourceChild.Manifold.Count > 0)
                        //{
                        //    Console.WriteLine($"Iffy dot: {testDot} NOT BLOCKED");
                        //}
                    }
                    else if (!faceFlagUnset)
                    {
                        //Clear the face flags. This isn't *required* since they're coherent enough anyway and the accumulated impulse redistributor is a decent fallback,
                        //but it costs basically nothing to do this.
                        for (int k = 0; k < sourceChild.Manifold.Count; ++k)
                        {
                            Unsafe.Add(ref sourceChild.Manifold.Contact0, k).FeatureId &= ~FaceCollisionFlag;
                        }
                    }
                }

                for (int i = 0; i < count; ++i)
                {
                    TryApplyBlockToTriangle(ref testTriangles.Values[i], children, meshOrientation, requiresFlip);
                }

                testTriangles.Dispose(pool);
                enumerator.List.Dispose(pool);
            }


        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryFlush<TCallbacks>(int pairId, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Debug.Assert(Inner.ChildCount > 0);
            if (Inner.CompletedChildCount == Inner.ChildCount)
            {
                Matrix3x3.CreateFromQuaternion(MeshOrientation, out var meshOrientation);
                Matrix3x3.Transpose(meshOrientation, out var meshInverseOrientation);
                //TODO: This is not flexible with respect to different mesh types. Not a problem right now, but it will be in the future.
                ReduceManifolds(ref Triangles, ref Inner.Children, 0, Inner.ChildCount, RequiresFlip, QueryBounds, meshOrientation, meshInverseOrientation, (Mesh*)Mesh, batcher.Pool);

                //Now that boundary smoothing analysis is done, we no longer need the triangle list.
                batcher.Pool.Return(ref Triangles);
                Inner.Flush(pairId, ref batcher);
                return true;
            }
            return false;
        }

    }
}
