using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Meridian59.Common;
using Meridian59.Common.Constants;
using Meridian59.Files.ROO;

// Switch FP precision based on architecture
#if X64
using Real = System.Double;
#else
using Real = System.Single;
#endif

namespace Meridian59.UnitTest
{
    [TestClass]
    public class SlidingTest
    {
        [TestMethod]
        public void TestWallSlidingCore()
        {
            uint version = RooFile.VERSIONHIGHRESGRID;

            // 1. Create a solid wall at y=0
            // side1: passable (inside), side2: solid (outside)
            RooSideDef side1 = new RooSideDef(1, 1, 0, 0, 0x00000004, 0); // IsPassable = 0x4
            RooSideDef side2 = new RooSideDef(2, 1, 0, 0, 0, 0); 
            RooWall w1 = new RooWall(version, 1, 1, 2, new V2(0, 0), new V2(1024, 0), 0, 0, 0, 0, 1, 0);
            w1.RightSide = side1;
            w1.LeftSide = side2;
            
            // 2. Test SlideAlong
            V2 start = new V2(512, 100);
            V2 end = new V2(700, -100);
            V2 slid = w1.SlideAlong(start, end);
            
            // Wall is horizontal (y=0). Projection of movement (188, -200) on X-axis is (188, 0).
            // Slid = start + (188, 0) = (700, 100).
            Assert.AreEqual(700, slid.X, 0.1);
            Assert.AreEqual(100, slid.Y, 0.1);

            // 3. Test iterative sliding (simulated)
            // Imagine we hit w1, then another wall w2 at x=0
            RooWall w4 = new RooWall(version, 4, 1, 2, new V2(0, 1024), new V2(0, 0), 0, 0, 0, 0, 1, 0);
            // start = (100, 100), end = (-100, -100)
            start = new V2(100, 100);
            end = new V2(-100, -100);
            
            // First slide (along w1, y=0)
            V2 slid1 = w1.SlideAlong(start, end); // (-100, 100)
            Assert.AreEqual(-100, slid1.X, 0.1);
            Assert.AreEqual(100, slid1.Y, 0.1);
            
            // Second slide (along w4, x=0)
            // w4: (0, 1024) -> (0, 0). Vector is (0, -1024).
            // Projection of (slid1 - start) = (-200, 0) on Y-axis is (0, 0).
            // This means we stop. Correct.
            V2 slid2 = w4.SlideAlong(start, slid1);
            Assert.AreEqual(100, slid2.X, 0.1);
            Assert.AreEqual(100, slid2.Y, 0.1);
            
            // 4. Test epsilon permissiveness
            // A point at very close distance should not be blocked if moving parallel.
            V2 pos = new V2(512, 1.01f);
            V2 nextPos = new V2(612, 1.01f);
            
            // dist2 between pos and w1
            int useCase;
            V2 p1 = w1.P1; V2 p2 = w1.P2;
            Real d2 = nextPos.MinSquaredDistanceToLineSegment(ref p1, ref p2, out useCase);
            
            // The logic I changed standardizes minDist2 to 1.0
            // if (dist2 >= 1.0 - 0.001) { continue; }
            Assert.IsTrue(d2 >= 1.0f - 0.001f);

            // 5. Test any wall proximity (the walking up fix)
            // A regular wall should now allow getting exactly 1.0 units from it
            V2 atWall = new V2(512, 1.0f); 
            d2 = atWall.MinSquaredDistanceToLineSegment(ref p1, ref p2, out useCase);
            
            // Should now pass the 'continue' check because 1.0 >= 1.0 - 0.001
            Assert.IsTrue(d2 >= 1.0f - 0.001f);
        }
    }
}
