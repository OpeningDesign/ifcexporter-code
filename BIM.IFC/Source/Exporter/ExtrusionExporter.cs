﻿//
// BIM IFC library: this library works with Autodesk(R) Revit(R) to export IFC files containing model geometry.
// Copyright (C) 2012  Autodesk, Inc.
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using BIM.IFC.Utility;
using BIM.IFC.Toolkit;

namespace BIM.IFC.Exporter
{
    /// <summary>
    /// Provides methods to export extrusions.
    /// </summary>
    class ExtrusionExporter
    {
        private static bool CurveLoopIsARectangle(CurveLoop curveLoop, out IList<int> cornerIndices)
        {
            cornerIndices = new List<int>(4);

            // looking for four orthogonal lines in one curve loop.
            int sz = curveLoop.Count();
            if (sz < 4)
                return false;
   
            IList<Line> lines = new List<Line>();
            foreach (Curve curve in curveLoop)
            {
                if (!(curve is Line))
                    return false;
      
                lines.Add(curve as Line);
            }

            sz = lines.Count;
            int numAngles = 0;
   
            // Must have 4 right angles found, and all other lines collinear -- if not, not a rectangle.
            for (int ii = 0; ii < sz; ii++)
            {
                double dot = lines[ii].Direction.DotProduct(lines[(ii+1)%sz].Direction);
                if (MathUtil.IsAlmostZero(dot))
                {
                    if (numAngles > 3)
                        return false;
                    cornerIndices.Add(ii);
                    numAngles++;
                }
                else if (MathUtil.IsAlmostEqual(dot, 1.0))
                {
                    XYZ line0End1 = lines[ii].get_EndPoint(1);
                    XYZ line1End0 = lines[(ii+1)%sz].get_EndPoint(0);
                    if (!line0End1.IsAlmostEqualTo(line1End0))
                        return false;
                }
                else
                    return false;
            }

            return (numAngles == 4);
        }

        private static IFCAnyHandle CreateRectangleProfileDefIfPossible(ExporterIFC exporterIFC, string profileName, CurveLoop curveLoop, Plane origPlane,
            XYZ projDir)
        {
            IList<int> cornerIndices = null;
            if (!CurveLoopIsARectangle(curveLoop, out cornerIndices))
                return null;
                
            IFCFile file = exporterIFC.GetFile();

            // for the RectangleProfileDef, we have a special requirement that if the profile is an opening
            // in a wall, then the "X" direction of the profile corresponds to the global Z direction (so
            // that reading applications can easily figure out height and width of the opening by reading the
            // X and Y lengths).  As such, we will look at the projection direction; if it is not wholly in the
            // Z direction (as it would be in the case of an opening in the floor, where this is irrelevant), then
            // we will modify the plane's X and Y axes as necessary to ensure that X corresponds to the "most" Z
            // direction (and Y still forms a right-handed coordinate system).
            XYZ xDir = origPlane.XVec;
            XYZ yDir = origPlane.YVec;
            XYZ zDir = origPlane.Normal;
            XYZ orig = origPlane.Origin;

            // if in Z-direction, or |x[2]| > |y[2]|, just use old plane.
            bool flipX = !MathUtil.IsAlmostEqual(Math.Abs(zDir[2]), 1.0) && (Math.Abs(xDir[2]) < Math.Abs(yDir[2]));

            IList<UV> polylinePts = new List<UV>();
            polylinePts.Add(new UV());
            double scale = exporterIFC.LinearScale;

            int idx = -1, whichCorner = 0;
            foreach (Curve curve in curveLoop)
            {
                idx++;
                if (cornerIndices[whichCorner] != idx)
                    continue;

                whichCorner++;
                Line line = curve as Line;

                XYZ point = line.get_EndPoint(1);
                UV pointProjUV = GeometryUtil.ProjectPointToPlane(origPlane, projDir, point);
                if (pointProjUV == null)
                    return null;
                pointProjUV *= scale;

                if (whichCorner == 4)
                {
                    polylinePts[0] = pointProjUV;
                    break;
                }
                else
                    polylinePts.Add(pointProjUV);
            }

            if (polylinePts.Count != 4)
                return null;

            // get the x and y length vectors.  We may have to reverse them.
            UV xLenVec = polylinePts[1] - polylinePts[0];
            UV yLenVec = polylinePts[3] - polylinePts[0];
            if (flipX)
            {
                UV tmp = xLenVec;
                xLenVec = yLenVec;
                yLenVec = tmp;
            }

            double xLen = xLenVec.GetLength();
            double yLen = yLenVec.GetLength();
            if (MathUtil.IsAlmostZero(xLen) || MathUtil.IsAlmostZero(yLen))
                return null;

            IList<double> middlePt = new List<double>();
            middlePt.Add((polylinePts[0].U + polylinePts[2].U) / 2);
            middlePt.Add((polylinePts[0].V + polylinePts[2].V) / 2);
            IFCAnyHandle location = IFCInstanceExporter.CreateCartesianPoint(file, middlePt);

            xLenVec = xLenVec.Normalize();
            IList<double> measure = new List<double>();
            measure.Add(xLenVec.U);
            measure.Add(xLenVec.V);
            IFCAnyHandle refDirectionOpt = ExporterUtil.CreateDirection(file, measure);

            IFCAnyHandle positionHnd = IFCInstanceExporter.CreateAxis2Placement2D(file, location, null, refDirectionOpt);

            IFCAnyHandle rectangularProfileDef = IFCInstanceExporter.CreateRectangleProfileDef(file, IFCProfileType.Area, profileName, positionHnd, xLen, yLen);
            return rectangularProfileDef;
        }

        private static bool GetCenterAndRadiusOfCurveLoop(CurveLoop curveLoop, out XYZ center, out double radius)
        {
            IList<Arc> arcs = new List<Arc>();
            center = new XYZ();
            radius = 0.0;

            foreach (Curve curve in curveLoop)
            {
                if (!(curve is Arc))
                    return false;

                arcs.Add(curve as Arc);
            }

            int numArcs = arcs.Count;
            if (numArcs == 0)
                return false;

            radius = arcs[0].Radius;
            center = arcs[0].Center;

            for (int ii = 1; ii < numArcs; ii++)
            {
                XYZ newCenter = arcs[ii].Center;
                if (!newCenter.IsAlmostEqualTo(center))
                    return false;
            }

            return true;
        }

        private static IFCAnyHandle CreateCircleProfileDefIfPossible(ExporterIFC exporterIFC, string profileName, CurveLoop curveLoop, Plane origPlane,
            XYZ projDir)
        {
            IList<CurveLoop> curveLoops = new List<CurveLoop>();
            curveLoops.Add(curveLoop);
            return CreateCircleProfileDefIfPossible(exporterIFC, profileName, curveLoops, origPlane, projDir);
        }

        private static IFCAnyHandle CreateCircleProfileDefIfPossible(ExporterIFC exporterIFC, string profileName, IList<CurveLoop> curveLoops, Plane origPlane,
            XYZ projDir)
        {
            int numLoops = curveLoops.Count;
            if (numLoops > 2)
                return null;

            IFCFile file = exporterIFC.GetFile();
	
            if (curveLoops[0].IsOpen() || (numLoops == 2 && curveLoops[1].IsOpen()))
                return null;

            XYZ origPlaneNorm = origPlane.Normal;
            Plane curveLoopPlane = null;
            try
            {
                curveLoopPlane = curveLoops[0].GetPlane();
            }
            catch
            {
                return null;
            }

            XYZ curveLoopPlaneNorm = curveLoopPlane.Normal;
            if (!MathUtil.IsAlmostEqual(Math.Abs(origPlaneNorm.DotProduct(curveLoopPlaneNorm)), 1.0))
                return null;

            if (numLoops == 2)
            {
                Plane secondCurveLoopPlane = null;
                try
                {
                    secondCurveLoopPlane = curveLoops[1].GetPlane();
                }
                catch
                {
                    return null;
                }

                XYZ secondCurveLoopPlaneNorm = secondCurveLoopPlane.Normal;
                if (!MathUtil.IsAlmostEqual(Math.Abs(curveLoopPlaneNorm.DotProduct(secondCurveLoopPlaneNorm)), 1.0))
                    return null;
            }

            IList<Arc> arcs = new List<Arc>();
            XYZ ctr;
            double radius, innerRadius = 0.0;
            if (!GetCenterAndRadiusOfCurveLoop(curveLoops[0], out ctr, out radius))
                return null;

            if (numLoops == 2)
            {
                XYZ checkCtr;
                if (!GetCenterAndRadiusOfCurveLoop(curveLoops[1], out checkCtr, out innerRadius))
                    return null;
                if (!ctr.IsAlmostEqualTo(checkCtr))
                    return null;
            }
        
            double scale = exporterIFC.LinearScale;
            radius *= scale;
			innerRadius *= scale;
   
            XYZ xDir = origPlane.XVec;
            XYZ yDir = origPlane.YVec;
            XYZ orig = origPlane.Origin;
            
            ctr -= orig;
            
            IList<double> newCtr = new List<double>();
            newCtr.Add(xDir.DotProduct(ctr) * scale);
            newCtr.Add(yDir.DotProduct(ctr) * scale);
      
		    IFCAnyHandle location = IFCInstanceExporter.CreateCartesianPoint(file, newCtr);
		    
            IList<double> refDir = new List<double>();
            refDir.Add(1.0);
            refDir.Add(0.0);
            IFCAnyHandle refDirectionOpt = ExporterUtil.CreateDirection(file, refDir);

		    IFCAnyHandle defPosition = IFCInstanceExporter.CreateAxis2Placement2D(file, location, null, refDirectionOpt);

            if (MathUtil.IsAlmostZero(innerRadius))
                return IFCInstanceExporter.CreateCircleProfileDef(file, IFCProfileType.Area, profileName, defPosition, radius);
            else
                return IFCInstanceExporter.CreateCircleHollowProfileDef(file, IFCProfileType.Area, profileName, defPosition, radius, radius-innerRadius);
	    }

        /// <summary>
        /// Determines if a curveloop can be exported as an I-Shape profile.
        /// </summary>
        /// <param name="exporterIFC">The exporter.</param>
        /// <param name="profileName">The name of the profile.</param>
        /// <param name="curveLoop">The curve loop.</param>
        /// <param name="origPlane">The plane of the loop.</param>
        /// <param name="projDir">The projection direction.</param>
        /// <returns>The IfcIShapeProfileDef, or null if not possible.</returns>
        /// <remarks>This routine works with I-shaped curveloops projected onto origPlane, in either orientation;
        /// it does not work with H-shaped curveloops.</remarks>
        private static IFCAnyHandle CreateIShapeProfileDefIfPossible(ExporterIFC exporterIFC, string profileName, CurveLoop curveLoop, Plane origPlane,
            XYZ projDir)
        {
            IFCFile file = exporterIFC.GetFile();

            if (curveLoop.IsOpen())
                return null;

            if (curveLoop.Count() != 12 && curveLoop.Count() != 16)
                return null;

            // All curves must be lines, except for 4 optional fillets; get direction vectors and start points.
            double scale = exporterIFC.LinearScale;
            XYZ xDir = origPlane.XVec;
            XYZ yDir = origPlane.YVec;

            // The list of vertices, in order.  startVertex below is the upper-right hand vertex, in UV-space.
            IList<UV> vertices = new List<UV>();    
            // The directions in UV of the line segments. directions[ii] is the direction of the line segment starting with vertex[ii].
            IList<UV> directions = new List<UV>();
            // The lengths in UV of the line segments.  lengths[ii] is the length of the line segment starting with vertex[ii].
            IList<double> lengths = new List<double>();
            // turnsCCW[ii] is true if directions[ii+1] is clockwise relative to directions[ii] in UV-space.
            IList<bool> turnsCCW = new List<bool>();
            
            IList<Arc> fillets = new List<Arc>();
            IList<int> filletPositions = new List<int>();

            int idx = 0;
            int startVertex = -1;
            int startFillet = -1;
            UV upperRight = null;
            double lowerBoundU = 1e+30;
            double upperBoundU = -1e+30;
            
            foreach (Curve curve in curveLoop)
            {
                if (!(curve is Line))
                {
                    if (!(curve is Arc))
                        return null;
                    fillets.Add(curve as Arc);
                    filletPositions.Add(idx);   // share the index of the next line segment.
                    continue;
                }

                Line line = curve as Line;

                XYZ point = line.get_EndPoint(0);
                UV pointProjUV = GeometryUtil.ProjectPointToPlane(origPlane, projDir, point);
                if (pointProjUV == null)
                    return null;
                pointProjUV *= scale;

                if ((upperRight == null) || ((pointProjUV.U > upperRight.U - MathUtil.Eps()) && (pointProjUV.V > upperRight.V - MathUtil.Eps())))
                {
                    upperRight = pointProjUV;
                    startVertex = idx;
                    startFillet = filletPositions.Count;
                }

                if (pointProjUV.U < lowerBoundU)
                    lowerBoundU = pointProjUV.U;
                if (pointProjUV.U > upperBoundU)
                    upperBoundU = pointProjUV.U;
                
                vertices.Add(pointProjUV);

                XYZ direction3d = line.Direction;
                UV direction = new UV(direction3d.DotProduct(xDir), direction3d.DotProduct(yDir));
                lengths.Add(line.Length * scale);
                
                bool zeroU = MathUtil.IsAlmostZero(direction.U);
                bool zeroV = MathUtil.IsAlmostZero(direction.V);
                if (zeroU && zeroV)
                    return null;

                // Accept only non-rotated I-Shapes.
                if (!zeroU && !zeroV)
                    return null;

                direction.Normalize();
                if (idx > 0)
                {
                    if (!MathUtil.IsAlmostZero(directions[idx - 1].DotProduct(direction)))
                        return null;
                    turnsCCW.Add(directions[idx - 1].CrossProduct(direction) > 0);
                }

                directions.Add(direction);
                idx++;
            }

            if (directions.Count != 12)
                return null;

            if (!MathUtil.IsAlmostZero(directions[11].DotProduct(directions[0])))
                return null;
            turnsCCW.Add(directions[11].CrossProduct(directions[0]) > 0);

            bool firstTurnIsCCW = turnsCCW[startVertex];

            // Check proper turning of lines.
            // The orientation of the turns should be such that 8 match the original orientation, and 4 go in the opposite direction.
            // The opposite ones are:
            // For I-Shape:
            // if the first turn is clockwise (i.e., in -Y direction): 1,2,7,8.
            // if the first turn is counterclockwise (i.e., in the -X direction): 2,3,8,9.
            // For H-Shape:
            // if the first turn is clockwise (i.e., in -Y direction): 2,3,8,9.
            // if the first turn is counterclockwise (i.e., in the -X direction): 1,2,7,8.
            
            int iShapeCCWOffset = firstTurnIsCCW ? 1 : 0;
            int hShapeCWOffset = firstTurnIsCCW ? 0 : 1;

            bool isIShape = true;
            bool isHShape = false;

            for (int ii = 0; ii < 12 && isIShape; ii++)
            {
                int currOffset = 12 + (startVertex - iShapeCCWOffset);
                int currIdx = (ii + currOffset) % 12;
                if (currIdx == 1 || currIdx == 2 || currIdx == 7 || currIdx == 8)
                {
                    if (firstTurnIsCCW == turnsCCW[ii])
                        isIShape = false;
                }
                else
                {
                    if (firstTurnIsCCW == !turnsCCW[ii])
                        isIShape = false;
                }
            }

            if (!isIShape)
            {
                // Check if it is orientated like an H - if neither I nor H, fail.
                isHShape = true;

                for (int ii = 0; ii < 12 && isHShape; ii++)
                {
                    int currOffset = 12 + (startVertex - hShapeCWOffset);
                    int currIdx = (ii + currOffset) % 12;
                    if (currIdx == 1 || currIdx == 2 || currIdx == 7 || currIdx == 8)
                    {
                        if (firstTurnIsCCW == turnsCCW[ii])
                            return null;
                    }
                    else
                    {
                        if (firstTurnIsCCW == !turnsCCW[ii])
                            return null;
                    }
                }
            }

            // Check that the lengths of parallel and symmetric line segments are equal.
            double overallWidth = 0.0;
            double overallDepth = 0.0;
            double flangeThickness = 0.0;
            double webThickness = 0.0;

            // I-Shape:
            // CCW pairs:(0,6), (1,5), (1,7), (1,11), (2,4), (2,8), (2,10), (3, 9)
            // CW pairs: (11,5), (0,4), (0,6), (0,10), (1,3), (1,7), (1,9), (2, 8)
            // H-Shape is reversed.
            int cwPairOffset = (firstTurnIsCCW == isIShape) ? 0 : 11;

            overallWidth = lengths[(startVertex + cwPairOffset) % 12];
            flangeThickness = lengths[(startVertex + 1 + cwPairOffset) % 12];

            if (isIShape)
            {
                if (firstTurnIsCCW)
                {
                    overallDepth = vertices[startVertex].V - vertices[(startVertex + 7) % 12].V;
                    webThickness = vertices[(startVertex + 9) % 12].U - vertices[(startVertex + 3) % 12].U;
                }
                else
                {
                    overallDepth = vertices[startVertex].V - vertices[(startVertex + 5) % 12].V;
                    webThickness = vertices[(startVertex + 2) % 12].U - vertices[(startVertex + 8) % 12].U;
                }
            }
            else
            {
                if (!firstTurnIsCCW)
                {
                    overallDepth = vertices[startVertex].U - vertices[(startVertex + 7) % 12].U;
                    webThickness = vertices[(startVertex + 9) % 12].V - vertices[(startVertex + 3) % 12].V;
                }
                else
                {
                    overallDepth = vertices[startVertex].U - vertices[(startVertex + 5) % 12].U;
                    webThickness = vertices[(startVertex + 2) % 12].V - vertices[(startVertex + 8) % 12].V;
                }
            }

            if (!MathUtil.IsAlmostEqual(overallWidth, lengths[(startVertex + 6 + cwPairOffset) % 12]))
                return null;
            if (!MathUtil.IsAlmostEqual(flangeThickness, lengths[(startVertex + 5 + cwPairOffset) % 12]) ||
                !MathUtil.IsAlmostEqual(flangeThickness, lengths[(startVertex + 7 + cwPairOffset) % 12]) ||
                !MathUtil.IsAlmostEqual(flangeThickness, lengths[(startVertex + 11 + cwPairOffset) % 12]))
                return null;
            double innerTopLeftLength = lengths[(startVertex + 2 + cwPairOffset) % 12];
            if (!MathUtil.IsAlmostEqual(innerTopLeftLength, lengths[(startVertex + 4 + cwPairOffset) % 12]) ||
                !MathUtil.IsAlmostEqual(innerTopLeftLength, lengths[(startVertex + 8 + cwPairOffset) % 12]) ||
                !MathUtil.IsAlmostEqual(innerTopLeftLength, lengths[(startVertex + 10 + cwPairOffset) % 12]))
                return null;
            double iShaftLength = lengths[(startVertex + 3 + cwPairOffset) % 12];
            if (!MathUtil.IsAlmostEqual(iShaftLength, lengths[(startVertex + 9 + cwPairOffset) % 12]))
                return null;
            
            // Check fillet validity.
            int numFillets = fillets.Count();
            double? filletRadius = null;

            if (numFillets != 0)
            {
                if (numFillets != 4)
                    return null;

                // startFillet can have any value from 0 to 4; if it is 4, need to reset it to 0.
                
                // The fillet positions relative to the upper right hand corner are:
                // For I-Shape:
                // if the first turn is clockwise (i.e., in -Y direction): 2,3,8,9.
                // if the first turn is counterclockwise (i.e., in the -X direction): 3,4,9,10.
                // For H-Shape:
                // if the first turn is clockwise (i.e., in -Y direction): 3,4,9,10.
                // if the first turn is counterclockwise (i.e., in the -X direction): 2,3,8,9.
                int filletOffset = (isIShape == firstTurnIsCCW) ? 1 : 0;
                if (filletPositions[startFillet % 4] != ((2 + filletOffset + startVertex) % 12) ||
                    filletPositions[(startFillet + 1) % 4] != ((3 + filletOffset + startVertex) % 12) ||
                    filletPositions[(startFillet + 2) % 4] != ((8 + filletOffset + startVertex) % 12) ||
                    filletPositions[(startFillet + 3) % 4] != ((9 + filletOffset + startVertex) % 12))
                    return null;

                double tmpFilletRadius = fillets[0].Radius;
                for (int ii = 1; ii < 4; ii++)
                {
                    if (!MathUtil.IsAlmostEqual(tmpFilletRadius, fillets[ii].Radius))
                        return null;
                }
                
                if (!MathUtil.IsAlmostZero(tmpFilletRadius))
                    filletRadius = tmpFilletRadius * scale;
            }
            
            XYZ planeNorm = origPlane.Normal;
            for (int ii = 0; ii < numFillets; ii++)
            {
                bool filletIsCCW = (fillets[ii].Normal.DotProduct(planeNorm) > MathUtil.Eps());
                if (filletIsCCW == firstTurnIsCCW)
                    return null;
            }

            if (MathUtil.IsAlmostZero(overallWidth) || MathUtil.IsAlmostZero(overallDepth) ||
                MathUtil.IsAlmostZero(flangeThickness) || MathUtil.IsAlmostZero(webThickness))
                return null;

            // We have an I-Shape Profile!
            IList<double> newCtr = new List<double>();
            newCtr.Add((vertices[0].U + vertices[6].U) / 2);
            newCtr.Add((vertices[0].V + vertices[6].V) / 2);

            IFCAnyHandle location = IFCInstanceExporter.CreateCartesianPoint(file, newCtr);

            IList<double> refDir = new List<double>();

            if (isIShape)
            {
                refDir.Add(1.0);
                refDir.Add(0.0);
            }
            else
            {
                refDir.Add(0.0);
                refDir.Add(1.0);
            }

            IFCAnyHandle refDirectionOpt = ExporterUtil.CreateDirection(file, refDir);

            IFCAnyHandle positionHnd = IFCInstanceExporter.CreateAxis2Placement2D(file, location, null, refDirectionOpt);

            return IFCInstanceExporter.CreateIShapeProfileDef(file, IFCProfileType.Area, profileName, positionHnd,
                overallWidth, overallDepth, webThickness, flangeThickness, filletRadius);
        }

        /// <returns>true if the curve loop is clockwise, false otherwise.</returns>
        private static bool SafeIsCurveLoopClockwise(CurveLoop curveLoop, XYZ dir)
        {
            if (curveLoop == null)
                return false;

            if (curveLoop.IsOpen())
                return false;

            if ((curveLoop.Count() == 1) && !(curveLoop.First().IsBound))
                return false;

            return !curveLoop.IsCounterclockwise(dir);
        }

        private static bool CorrectCurveLoopOrientation(IList<CurveLoop> curveLoops, XYZ extrDir, out Plane plane)
        {
            plane = null;
            int loopSz = curveLoops.Count;
            bool firstCurve = true;
            foreach (CurveLoop curveLoop in curveLoops)
            {
                // ignore checks if unbounded curve.
                if (curveLoop.Count() == 0)
                    return false;

                if (!(curveLoop.First().IsBound))
                {
                    if (firstCurve)
                    {
                        Arc arc = curveLoop.First() as Arc;
                        if (arc == null)
                            return false;

                        XYZ xVec = arc.XDirection;
                        XYZ yVec = arc.YDirection;
                        XYZ center = arc.Center;

                        plane = new Plane(xVec, yVec, center);
                    }
                }
                else if (firstCurve)
                {
                    if (SafeIsCurveLoopClockwise(curveLoop, extrDir))
                        curveLoop.Flip();

                    try
                    {
                        plane = curveLoop.GetPlane();
                    }
                    catch
                    {
                        return false;
                    }
                    if (plane == null)
                        return false;
                }
                else
                {
                    if (!SafeIsCurveLoopClockwise(curveLoop, extrDir))
                        curveLoop.Flip();
                }

                firstCurve = false;
            }
            
            return (plane != null);
        }

        /// <summary>
        /// Creates an extruded solid from a collection of curve loops and a thickness.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC object.</param>
        /// <param name="profileName">The name of the extrusion profile.</param>
        /// <param name="origCurveLoops">The profile boundary curves.</param>
        /// <param name="plane">The plane of the boundary curves.</param>
        /// <param name="extrDirVec">The direction of the extrusion.</param>
        /// <param name="scaledExtrusionSize">The thickness of the extrusion, perpendicular to the plane.</param>
        /// <returns>The IfcExtrudedAreaSolid handle.</returns>
        /// <remarks>If the curveLoop plane normal is not the same as the plane direction, only tesellated boundaries are supported.</remarks> 
        public static IFCAnyHandle CreateExtrudedSolidFromCurveLoop(ExporterIFC exporterIFC, string profileName, IList<CurveLoop> origCurveLoops,
            Plane plane, XYZ extrDirVec, double scaledExtrusionSize)
        {
            IFCAnyHandle extrudedSolidHnd = null;
            
            if (scaledExtrusionSize < MathUtil.Eps())
                return extrudedSolidHnd;

            IFCFile file = exporterIFC.GetFile();
	
            // we need to figure out the plane of the curve loops and modify the extrusion direction appropriately.
            // assumption: first curve loop defines the plane.
            int sz = origCurveLoops.Count;
            if (sz == 0)
                return extrudedSolidHnd;

            XYZ planeXDir = plane.XVec;
            XYZ planeYDir = plane.YVec;
            XYZ planeZDir = plane.Normal;
            XYZ planeOrig = plane.Origin;

            double slantFactor = Math.Abs(planeZDir.DotProduct(extrDirVec));
            if (MathUtil.IsAlmostZero(slantFactor))
                return extrudedSolidHnd;

            // Check that curve loops are valid.
            IList<CurveLoop> curveLoops = ExporterIFCUtils.ValidateCurveLoops(origCurveLoops, extrDirVec);
            if (curveLoops.Count == 0)
                return extrudedSolidHnd;

            scaledExtrusionSize /= slantFactor;

            IFCAnyHandle sweptArea = CreateSweptArea(exporterIFC, profileName, curveLoops, plane, extrDirVec);
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(sweptArea))
                return extrudedSolidHnd;

            IList<double> relExtrusionDirList = new List<double>();
            relExtrusionDirList.Add(extrDirVec.DotProduct(planeXDir));
            relExtrusionDirList.Add(extrDirVec.DotProduct(planeYDir));
            relExtrusionDirList.Add(extrDirVec.DotProduct(planeZDir));

            XYZ scaledXDir = ExporterIFCUtils.TransformAndScaleVector(exporterIFC, planeXDir);
            XYZ scaledZDir = ExporterIFCUtils.TransformAndScaleVector(exporterIFC, planeZDir);
            XYZ scaledOrig = ExporterIFCUtils.TransformAndScalePoint(exporterIFC, planeOrig);
   
            IFCAnyHandle solidAxis = ExporterUtil.CreateAxis(file, scaledOrig, scaledZDir, scaledXDir);
            IFCAnyHandle extrusionDirection = ExporterUtil.CreateDirection(file, relExtrusionDirList);

            extrudedSolidHnd = IFCInstanceExporter.CreateExtrudedAreaSolid(file, sweptArea, solidAxis, extrusionDirection, scaledExtrusionSize);
            return extrudedSolidHnd;
        }

        /// <summary>
        /// Creates an IfcProfileDef for a swept area.
        /// </summary>
        /// <param name="exporterIFC">The exporter.</param>
        /// <param name="profileName">The profile name.</param>
        /// <param name="curveLoops">The curve loops.</param>
        /// <param name="plane">The plane of the curve loops.</param>
        /// <param name="sweptDirection">The direction.</param>
        /// <returns>The handle.</returns>
        public static IFCAnyHandle CreateSweptArea(ExporterIFC exporterIFC, string profileName, IList<CurveLoop> curveLoops, Plane plane, XYZ sweptDirection)
        {
            IFCAnyHandle sweptArea = null;
            if (curveLoops.Count == 1)
            {
                sweptArea = CreateRectangleProfileDefIfPossible(exporterIFC, profileName, curveLoops[0], plane, sweptDirection);
                if (sweptArea == null) sweptArea = CreateCircleProfileDefIfPossible(exporterIFC, profileName, curveLoops[0], plane, sweptDirection);
                if (sweptArea == null) sweptArea = CreateIShapeProfileDefIfPossible(exporterIFC, profileName, curveLoops[0], plane, sweptDirection);
            }
            else if (curveLoops.Count == 2)
            {
                sweptArea = CreateCircleProfileDefIfPossible(exporterIFC, profileName, curveLoops, plane, sweptDirection);
            }

            if (sweptArea == null)
            {
                IFCAnyHandle profileCurve = null;
                HashSet<IFCAnyHandle> innerCurves = new HashSet<IFCAnyHandle>();

                // reorient curves if necessary: outer CCW, inners CW.
                foreach (CurveLoop curveLoop in curveLoops)
                {
                    bool isCCW = false;
                    try
                    {
                        isCCW = curveLoop.IsCounterclockwise(plane.Normal);
                    }
                    catch
                    {
                        if (profileCurve == null)
                            return null;
                        else
                            continue;
                    }

                    if (profileCurve == null)
                    {
                        if (!isCCW)
                            curveLoop.Flip();
                        profileCurve = ExporterIFCUtils.CreateCurveFromCurveLoop(exporterIFC, curveLoop, plane, sweptDirection);
                        if (IFCAnyHandleUtil.IsNullOrHasNoValue(profileCurve))
                            return null;
                    }
                    else
                    {
                        if (isCCW)
                            curveLoop.Flip();
                        IFCAnyHandle innerCurve = ExporterIFCUtils.CreateCurveFromCurveLoop(exporterIFC, curveLoop, plane, sweptDirection);
                        if (!IFCAnyHandleUtil.IsNullOrHasNoValue(innerCurve))
                            innerCurves.Add(innerCurve);
                    }
                }

                IFCFile file = exporterIFC.GetFile();
                if (innerCurves.Count > 0)
                    sweptArea = IFCInstanceExporter.CreateArbitraryProfileDefWithVoids(file, IFCProfileType.Area, profileName, profileCurve, innerCurves);
                else
                    sweptArea = IFCInstanceExporter.CreateArbitraryClosedProfileDef(file, IFCProfileType.Area, profileName, profileCurve);
            }
            return sweptArea;
        }
 
        /// <summary>
        /// Creates extruded solid from extrusion data.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC object.</param>
        /// <param name="extrusionData">The extrusion data.</param>
        /// <returns>The IfcExtrudedAreaSolid handle.</returns>
        public static IFCAnyHandle CreateExtrudedSolidFromExtrusionData(ExporterIFC exporterIFC, Element element, IFCExtrusionData extrusionData)
        {
            if (!extrusionData.IsValid())
                return null;

            IList<CurveLoop> extrusionLoops = extrusionData.GetLoops();
            if (extrusionLoops != null)
            {
                XYZ extrusionDir = extrusionData.ExtrusionDirection;
                double extrusionSize = extrusionData.ScaledExtrusionLength;

                Plane plane = null;
                if (CorrectCurveLoopOrientation(extrusionLoops, extrusionDir, out plane))
                {
                    string profileName = null;
                    if (element != null)
                    {
                        ElementType type = element.Document.GetElement(element.GetTypeId()) as ElementType;
                        if (type != null)
                            profileName = type.Name;
                    }
                    IFCAnyHandle extrudedSolid = CreateExtrudedSolidFromCurveLoop(exporterIFC, profileName, extrusionLoops,
                        plane, extrusionDir, extrusionSize);
                    return extrudedSolid;
                }
            }

            return null;
        }

        /// <summary>
        /// Computes height and width of a curve loop with respect to the projection plane.
        /// </summary>
        /// <param name="curveLoop">The curve loop.</param>
        /// <param name="plane">The projection plane.</param>
        /// <param name="height">The height.</param>
        /// <param name="width">The width.</param>
        /// <returns>True if success, false if fail.</returns>
        public static bool ComputeHeightWidthOfCurveLoop(CurveLoop curveLoop, Plane plane, out double height, out double width)
        {
            height = 0.0;
            width = 0.0;

            Plane localPlane = plane;
            if (localPlane == null)
            {
                try
                {
                    localPlane = curveLoop.GetPlane();
                }
                catch
                {
                    return false;
                }
            }

            if (curveLoop.IsRectangular(localPlane))
            {
                height = curveLoop.GetRectangularHeight(localPlane);
                width = curveLoop.GetRectangularWidth(localPlane);
                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// Computes the outer length of curve loops.
        /// </summary>
        /// <param name="curveLoops">List of curve loops.</param>
        /// <returns>The length.</returns>
        public static double ComputeOuterPerimeterOfCurveLoops(IList<CurveLoop> curveLoops)
        {
            int numCurveLoops = curveLoops.Count;
            if (numCurveLoops == 0)
                return 0.0;

            if (curveLoops[0].IsOpen())
                return 0.0;

            return curveLoops[0].GetExactLength();
        }

        /// <summary>
        /// Computes the inner length of curve loops.
        /// </summary>
        /// <param name="curveLoops">List of curve loops.</param>
        /// <returns>The length.</returns>
        public static double ComputeInnerPerimeterOfCurveLoops(IList<CurveLoop> curveLoops)
        {
            double innerPerimeter = 0.0;

            int numCurveLoops = curveLoops.Count;
            if (numCurveLoops == 0)
                return 0.0;

            for (int ii = 1; ii < numCurveLoops; ii++)
            {
                if (curveLoops[ii].IsOpen())
                    return 0.0;
                innerPerimeter += curveLoops[ii].GetExactLength();
            }

            return innerPerimeter;
        }

        /// <summary>
        /// Adds a new opening to extrusion creation data from curve loop and extrusion data.
        /// </summary>
        /// <param name="creationData">The extrusion creation data.</param>
        /// <param name="from">The extrusion data.</param>
        /// <param name="curveLoop">The curve loop.</param>
        public static void AddOpeningData(IFCExtrusionCreationData creationData, IFCExtrusionData from, CurveLoop curveLoop)
        {
            List<CurveLoop> curveLoops = new List<CurveLoop>();
            curveLoops.Add(curveLoop);
            AddOpeningData(creationData, from, curveLoops);
        }

        /// <summary>
        /// Adds a new opening to extrusion creation data from extrusion data.
        /// </summary>
        /// <param name="creationData">The extrusion creation data.</param>
        /// <param name="from">The extrusion data.</param>
        public static void AddOpeningData(IFCExtrusionCreationData creationData, IFCExtrusionData from)
        {
            AddOpeningData(creationData, from, from.GetLoops());
        }

        /// <summary>
        /// Adds a new opening to extrusion creation data from curve loops and extrusion data.
        /// </summary>
        /// <param name="creationData">The extrusion creation data.</param>
        /// <param name="from">The extrusion data.</param>
        /// <param name="curveLoops">The curve loops.</param>
        public static void AddOpeningData(IFCExtrusionCreationData creationData, IFCExtrusionData from, ICollection<CurveLoop> curveLoops)
        {
            IFCExtrusionData newData = new IFCExtrusionData();
            foreach (CurveLoop curveLoop in curveLoops)
                newData.AddLoop(curveLoop);
            newData.ScaledExtrusionLength = from.ScaledExtrusionLength;
            newData.ExtrusionBasis = from.ExtrusionBasis;

            newData.ExtrusionDirection = from.ExtrusionDirection;
            creationData.AddOpening(newData);
        }

        /// <summary>
        /// Generates an IFCExtrusionCreationData from ExtrusionAnalyzer results
        /// </summary>
        /// <remarks>This will be used to populate certain property sets.</remarks>
        /// <param name="exporterIFC">The exporter.</param>
        /// <param name="projDir">The projection direction of the extrusion.</param>
        /// <param name="analyzer">The extrusion analyzer.</param>
        /// <returns>The IFCExtrusionCreationData information.</returns>
        public static IFCExtrusionCreationData GetExtrusionCreationDataFromAnalyzer(ExporterIFC exporterIFC, XYZ projDir, ExtrusionAnalyzer analyzer)
        {
            IFCExtrusionCreationData exportBodyParams = new IFCExtrusionCreationData();

            XYZ extrusionDirection = analyzer.ExtrusionDirection;
            double scale = exporterIFC.LinearScale;

            double zOff = MathUtil.IsAlmostEqual(Math.Abs(projDir[2]), 1.0) ? (1.0 - Math.Abs(extrusionDirection[2])) : Math.Abs(extrusionDirection[2]);
            double scaledAngle = Math.Asin(zOff) * 180 / Math.PI;
            
            exportBodyParams.Slope = scaledAngle;
            exportBodyParams.ScaledLength = (analyzer.EndParameter - analyzer.StartParameter) * scale;
            exportBodyParams.ExtrusionDirection = extrusionDirection;
            
            // no opening data support yet.

            Face extrusionBase = analyzer.GetExtrusionBase();
            if (extrusionBase == null)
                return null;

            IList<GeometryUtil.FaceBoundaryType> boundaryTypes;
            IList<CurveLoop> boundaries = GeometryUtil.GetFaceBoundaries(extrusionBase, XYZ.Zero, out boundaryTypes);
            if (boundaries.Count == 0)
                return null;

            Plane plane = null;
            double height = 0.0, width = 0.0;
            if (ExtrusionExporter.ComputeHeightWidthOfCurveLoop(boundaries[0], plane, out height, out width))
            {
                exportBodyParams.ScaledHeight = height * scale;
                exportBodyParams.ScaledWidth = width * scale;
            }

            double area = extrusionBase.Area;
            if (area > 0.0)
            {
                exportBodyParams.ScaledArea = area * scale * scale;
            }

            double innerPerimeter = ExtrusionExporter.ComputeInnerPerimeterOfCurveLoops(boundaries);
            double outerPerimeter = ExtrusionExporter.ComputeOuterPerimeterOfCurveLoops(boundaries);
            if (innerPerimeter > 0.0)
                exportBodyParams.ScaledInnerPerimeter = innerPerimeter * scale;
            if (outerPerimeter > 0.0)
                exportBodyParams.ScaledOuterPerimeter = outerPerimeter * scale;

            return exportBodyParams;
        }

        private class HandleAndAnalyzer
        {
            public IFCAnyHandle Handle = null;
            public ExtrusionAnalyzer Analyzer = null;
        }


        private static HandleAndAnalyzer CreateExtrusionWithClippingBase(ExporterIFC exporterIFC, Element element, 
            ElementId catId, IList<Solid> solids, Plane plane, XYZ projDir, IFCRange range, out bool completelyClipped, out HashSet<ElementId> materialIds)
        {
            IFCFile file = exporterIFC.GetFile();
            using (IFCTransaction tr = new IFCTransaction(file))
            {
                completelyClipped = false;
                materialIds = new HashSet<ElementId>();
                HandleAndAnalyzer retVal = new HandleAndAnalyzer();
                HashSet<IFCAnyHandle> extrusionBodyItems = new HashSet<IFCAnyHandle>();
                HashSet<IFCAnyHandle> extrusionBooleanBodyItems = new HashSet<IFCAnyHandle>();
                HashSet<IFCAnyHandle> extrusionClippingBodyItems = new HashSet<IFCAnyHandle>();
                foreach (Solid solid in solids)
                {
                    bool hasClippingResult = false;
                    bool hasBooleanResult = false;
                    ElementId materialId = ElementId.InvalidElementId;
                    retVal = CreateExtrsionWithClippingAndOpening(exporterIFC, element, catId, solid, plane, projDir, range,
                        out completelyClipped, out hasClippingResult, out hasBooleanResult, out materialId);

                    if (retVal != null && retVal.Handle != null)
                    {
                        materialIds.Add(materialId);
                        IFCAnyHandle repHandle = retVal.Handle;
                        if (hasBooleanResult) // if both have boolean and clipping result, use boolean one.
                            extrusionBooleanBodyItems.Add(repHandle);
                        else if (hasClippingResult)
                            extrusionClippingBodyItems.Add(repHandle);
                        else
                            extrusionBodyItems.Add(repHandle);

                    }
                    else
                    {
                        tr.RollBack();

                        // TODO: include this cleanup in RollBack(), to avoid issues.
                        ExporterCacheManager.MaterialIdToStyleHandleCache.RemoveHandles(materialIds);
                        ExporterCacheManager.PresentationStyleAssignmentCache.RemoveHandles(materialIds);
                        return retVal;
                    }
                }

                IFCAnyHandle contextOfItemsBody = exporterIFC.Get3DContextHandle("Body");

                if (extrusionBodyItems.Count > 0 && (extrusionClippingBodyItems.Count == 0 && extrusionBooleanBodyItems.Count == 0))
                {
                    retVal.Handle = RepresentationUtil.CreateSweptSolidRep(exporterIFC, element, catId, contextOfItemsBody,
                        extrusionBodyItems, null);
                }
                else if (extrusionClippingBodyItems.Count > 0 && (extrusionBodyItems.Count == 0 && extrusionBooleanBodyItems.Count == 0))
                {
                    retVal.Handle = RepresentationUtil.CreateClippingRep(exporterIFC, element, catId, contextOfItemsBody,
                        extrusionClippingBodyItems);
                }
                else if (extrusionBooleanBodyItems.Count > 0 && (extrusionBodyItems.Count == 0 && extrusionClippingBodyItems.Count == 0))
                {
                    retVal.Handle = RepresentationUtil.CreateCSGRep(exporterIFC, element, catId, contextOfItemsBody,
                        extrusionBooleanBodyItems);
                }
                else
                {
                    IFCAnyHandle finalBodyItemHnd = null;

                    ICollection<IFCAnyHandle> booleanBodyItems = extrusionClippingBodyItems.Union<IFCAnyHandle>(extrusionBooleanBodyItems).ToList();

                    finalBodyItemHnd = booleanBodyItems.ElementAt(0);
                    booleanBodyItems.Remove(finalBodyItemHnd);

                    // union non-boolean result first with a boolean result
                    foreach (IFCAnyHandle bodyRep in extrusionBodyItems)
                    {
                        finalBodyItemHnd = IFCInstanceExporter.CreateBooleanResult(exporterIFC.GetFile(), IFCBooleanOperator.Union,
                             finalBodyItemHnd, bodyRep);
                    }

                    foreach (IFCAnyHandle bodyRep in booleanBodyItems)
                    {
                        finalBodyItemHnd = IFCInstanceExporter.CreateBooleanResult(exporterIFC.GetFile(), IFCBooleanOperator.Union,
                             finalBodyItemHnd, bodyRep);
                    }

                    extrusionBodyItems.Clear();
                    extrusionBodyItems.Add(finalBodyItemHnd);
                    retVal.Handle = RepresentationUtil.CreateCSGRep(exporterIFC, element, catId, contextOfItemsBody,
                        extrusionBodyItems);
                }
                tr.Commit();
                return retVal;
            }
        }

        private static HandleAndAnalyzer CreateExtrsionWithClippingAndOpening(ExporterIFC exporterIFC, Element element, 
            ElementId catId, Solid solid, Plane plane, XYZ projDir, IFCRange range, out bool completelyClipped,
            out bool hasClippingResult, out bool hasBooleanResult, out ElementId materialId)
        {
            completelyClipped = false;
            materialId = ElementId.InvalidElementId;
            hasClippingResult = false;
            hasBooleanResult = false;
            HandleAndAnalyzer nullVal = new HandleAndAnalyzer();
            HandleAndAnalyzer retVal = new HandleAndAnalyzer();

            try
            {
                ExtrusionAnalyzer elementAnalyzer = ExtrusionAnalyzer.Create(solid, plane, projDir);
                retVal.Analyzer = elementAnalyzer;
            
                Document document = element.Document;
                double scale = exporterIFC.LinearScale;
                XYZ planeOrig = plane.Origin;
                XYZ baseLoopOffset = null;
          
                if (!MathUtil.IsAlmostZero(elementAnalyzer.StartParameter))
                    baseLoopOffset = elementAnalyzer.StartParameter * projDir;
                
                Face extrusionBase = elementAnalyzer.GetExtrusionBase();

                IList<GeometryUtil.FaceBoundaryType> boundaryTypes;
                IList<CurveLoop> extrusionBoundaryLoops =
                    GeometryUtil.GetFaceBoundaries(extrusionBase, baseLoopOffset, out boundaryTypes);

                // Return if we get any CurveLoops that are complex, as we don't want to export an approximation of the boundary here.
                foreach (GeometryUtil.FaceBoundaryType boundaryType in boundaryTypes)
                {
                    if (boundaryType == GeometryUtil.FaceBoundaryType.Complex)
                        return nullVal;
                }

                // Move base plane to start parameter location.
                Plane extrusionBasePlane = null;
                try
                {
                    extrusionBasePlane = extrusionBoundaryLoops[0].GetPlane();
                }
                catch
                {
                    return nullVal;
                }

                double extrusionLength = elementAnalyzer.EndParameter - elementAnalyzer.StartParameter;
                double baseOffset = extrusionBasePlane.Origin.DotProduct(projDir);
                IFCRange extrusionRange = new IFCRange(baseOffset, extrusionLength + baseOffset);

                double startParam = planeOrig.DotProduct(projDir);
                double endParam = planeOrig.DotProduct(projDir) + extrusionLength;
                if ((range != null) && (startParam >= range.End || endParam <= range.Start))
                {
                    completelyClipped = true;
                    return nullVal;
                }

                double scaledExtrusionDepth = extrusionLength * scale;

                string profileName = null;
                if (element != null)
                {
                    ElementType type = element.Document.GetElement(element.GetTypeId()) as ElementType;
                    if (type != null)
                        profileName = type.Name;
                }

                // We use a sub-transaction here in case we are able to generate the base body but not the clippings.
                IFCFile file = exporterIFC.GetFile();
                IFCAnyHandle finalExtrusionBodyItemHnd = null;
                using (IFCTransaction tr = new IFCTransaction(file))
                {
                    // For creating the actual extrusion, we want to use the calculated extrusion plane, not the input plane.
                    IFCAnyHandle extrusionBodyItemHnd = ExtrusionExporter.CreateExtrudedSolidFromCurveLoop(exporterIFC, profileName,
                        extrusionBoundaryLoops, extrusionBasePlane, projDir, scaledExtrusionDepth);
                    if (!IFCAnyHandleUtil.IsNullOrHasNoValue(extrusionBodyItemHnd))
                    {
                        finalExtrusionBodyItemHnd = extrusionBodyItemHnd;
                        IDictionary<ElementId, ICollection<ICollection<Face>>> elementCutouts =
                            GeometryUtil.GetCuttingElementFaces(element, elementAnalyzer);
                        foreach (KeyValuePair<ElementId, ICollection<ICollection<Face>>> elementCutoutsForElement in elementCutouts)
                        {
                            // process clippings first, then openings
                            ICollection<ICollection<Face>> unhandledElementCutoutsForElement = new List<ICollection<Face>>();
                            Element cuttingElement = document.GetElement(elementCutoutsForElement.Key);
                            foreach (ICollection<Face> elementCutout in elementCutoutsForElement.Value)
                            {
                                bool unhandledClipping = false;
                                try
                                {
                                    finalExtrusionBodyItemHnd = GeometryUtil.CreateClippingFromFaces(exporterIFC, cuttingElement, 
                                        extrusionBasePlane, projDir,
                                        elementCutout, extrusionRange, finalExtrusionBodyItemHnd);
                                }
                                catch
                                {
                                    unhandledClipping = true;
                                }

                                if (finalExtrusionBodyItemHnd == null || unhandledClipping)
                                {
                                    unhandledElementCutoutsForElement.Add(elementCutout);
                                }
                                else if (finalExtrusionBodyItemHnd != extrusionBodyItemHnd)
                                {
                                    hasClippingResult = true;
                                }
                            }

                            IFCAnyHandle finalExtrusionClippingBodyItemHnd = finalExtrusionBodyItemHnd;
                            foreach (ICollection<Face> elementCutout in unhandledElementCutoutsForElement)
                            {
                                bool unhandledOpening = false;
                                try
                                {
                                    finalExtrusionBodyItemHnd = GeometryUtil.CreateOpeningFromFaces(exporterIFC, cuttingElement,
                                        extrusionBasePlane, projDir,
                                        elementCutout, extrusionRange, finalExtrusionBodyItemHnd);
                                }
                                catch
                                {
                                    unhandledOpening = true;
                                }

                                if (finalExtrusionBodyItemHnd == null || unhandledOpening)
                                {
                                    // Item is completely clipped.
                                    completelyClipped = (finalExtrusionBodyItemHnd == null);
                                    tr.RollBack();
                                    return nullVal;
                                }
                                else if (finalExtrusionBodyItemHnd != finalExtrusionClippingBodyItemHnd)
                                {
                                    hasBooleanResult = true;
                                }
                            }
                        }

                        materialId = BodyExporter.GetBestMaterialIdFromGeometryOrParameter(solid, exporterIFC, element);
                        BodyExporter.CreateSurfaceStyleForRepItem(exporterIFC, document, extrusionBodyItemHnd, materialId);
                    }
                    tr.Commit();
                }

                retVal.Handle = finalExtrusionBodyItemHnd;
                return retVal;
            }
            catch
            {
                return nullVal;
            }
        }

        /// <summary>
        /// Creates an extrusion with potential clipping from a solid representation of an element.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC class.</param>
        /// <param name="element">The element.</param>
        /// <param name="catId">The category of the element and/or the solid geometry.</param>
        /// <param name="solid">The solid geometry.</param>
        /// <param name="plane">The extrusion base plane.</param>
        /// <param name="projDir">The projection direction.</param>
        /// <param name="range">The upper and lower limits of the extrusion, in the projection direction.</param>
        /// <param name="completelyClipped">Returns true if the extrusion is completely outside the range.</param>
        /// <returns>The extrusion handle.</returns>
        public static IFCAnyHandle CreateExtrusionWithClipping(ExporterIFC exporterIFC, Element element, ElementId catId,
            Solid solid, Plane plane, XYZ projDir, IFCRange range, out bool completelyClipped)
        {
            IList<Solid> solids = new List<Solid>();
            solids.Add(solid);
            HashSet<ElementId> materialIds = null;
            HandleAndAnalyzer handleAndAnalyzer = CreateExtrusionWithClippingBase(exporterIFC, element, catId,
                solids, plane, projDir, range, out completelyClipped, out materialIds);
            return handleAndAnalyzer.Handle;
        }


        /// <summary>
        /// Creates an extrusion with potential clipping from a list of solids corresponding to an element.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC class.</param>
        /// <param name="element">The element.</param>
        /// <param name="catId">The category of the element and/or the solid geometry.</param>
        /// <param name="solids">The list of solid geometries.</param>
        /// <param name="plane">The extrusion base plane.</param>
        /// <param name="projDir">The projection direction.</param>
        /// <param name="range">The upper and lower limits of the extrusion, in the projection direction.</param>
        /// <param name="completelyClipped">Returns true if the extrusion is completely outside the range.</param>
        /// <param name="materialIds">The material ids of the solid geometry.</param>
        /// <returns>The extrusion handle.</returns>
        public static IFCAnyHandle CreateExtrusionWithClipping(ExporterIFC exporterIFC, Element element, ElementId catId,
            IList<Solid> solids, Plane plane, XYZ projDir, IFCRange range, out bool completelyClipped, out HashSet<ElementId> materialIds)
        {
            HandleAndAnalyzer handleAndAnalyzer = CreateExtrusionWithClippingBase(exporterIFC, element, catId,
                solids, plane, projDir, range, out completelyClipped, out materialIds);
            return handleAndAnalyzer.Handle;
        }

        /// <summary>
        /// Creates an extrusion with potential clipping from a solid corresponding to an element, and supplies ExtrusionCreationData for the result.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC class.</param>
        /// <param name="element">The element.</param>
        /// <param name="catId">The category of the element and/or the solid geometry.</param>
        /// <param name="solid">The solid geometry.</param>
        /// <param name="plane">The extrusion base plane.</param>
        /// <param name="projDir">The projection direction.</param>
        /// <param name="range">The upper and lower limits of the extrusion, in the projection direction.</param>
        /// <param name="completelyClipped">Returns true if the extrusion is completely outside the range.</param>
        /// <returns>The extrusion handle.</returns>
        public static HandleAndData CreateExtrusionWithClippingAndProperties(ExporterIFC exporterIFC, 
            Element element, ElementId catId, Solid solid, Plane plane, XYZ projDir, IFCRange range, out bool completelyClipped)
        {
            IList<Solid> solids = new List<Solid>();
            solids.Add(solid);
            HashSet<ElementId> materialIds = null;
            HandleAndAnalyzer handleAndAnalyzer = CreateExtrusionWithClippingBase(exporterIFC, element, catId,
                solids, plane, projDir, range, out completelyClipped, out materialIds);

            HandleAndData ret = new HandleAndData();
            ret.Handle = handleAndAnalyzer.Handle;
            if (handleAndAnalyzer.Analyzer != null)
            {
                ret.Data = GetExtrusionCreationDataFromAnalyzer(exporterIFC, projDir, handleAndAnalyzer.Analyzer);
            }

            return ret;
        }

        /// <summary>
        /// Creates an extruded surface of type IfcSurfaceOfLinearExtrusion given a base 2D curve, a direction and a length.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC class.</param>
        /// <param name="baseCurve">The curve to be extruded.</param>
        /// <param name="extrusionDir">The direction of the extrusion.</param>
        /// <param name="scaledExtrusionSize">The length of the extrusion, in IFC unit scale.</param>
        /// <param name="unscaledBaseHeight">The Z value of the base level for the surface, in Revit unit scale.</param>
        /// <returns>The extrusion handle.</returns>
        /// <remarks>Note that scaledExtrusionSize and unscaledBaseHeight are in potentially different scaling units.</remarks>
        public static IFCAnyHandle CreateExtrudedSurfaceFromCurve(ExporterIFC exporterIFC, Curve baseCurve, XYZ extrusionDir,
            double scaledExtrusionSize, double unscaledBaseHeight)
        {
            IFCFile file = exporterIFC.GetFile();

            Plane plane = new Plane(extrusionDir,XYZ.Zero);

            // A list of IfcCurve entities.
            IFCGeometryInfo info = IFCGeometryInfo.CreateCurveGeometryInfo(exporterIFC, plane, extrusionDir, true);
            ExporterIFCUtils.CollectGeometryInfo(exporterIFC, info, baseCurve, XYZ.Zero, true);

            IList<IFCAnyHandle> profileCurves = info.GetCurves();

            if ((profileCurves.Count != 1) || (!IFCAnyHandleUtil.IsSubTypeOf(profileCurves[0], IFCEntityType.IfcBoundedCurve)))
                return null;

            IFCAnyHandle sweptCurve = IFCInstanceExporter.CreateArbitraryOpenProfileDef(file, IFCProfileType.Curve, null, profileCurves[0]);

            XYZ oCurveOrig = baseCurve.get_EndPoint(0);
            XYZ orig = (new XYZ(0.0, 0.0, oCurveOrig[2] - unscaledBaseHeight)) * exporterIFC.LinearScale;

            IFCAnyHandle surfaceAxis = ExporterUtil.CreateAxis(file, orig, null, null);
            IFCAnyHandle direction = ExporterUtil.CreateDirection(file, extrusionDir);     // zDir

            IFCAnyHandle surfOnRelatingElement = IFCInstanceExporter.CreateSurfaceOfLinearExtrusion(file, sweptCurve, surfaceAxis, direction, scaledExtrusionSize);

            IFCAnyHandle extrudedSurfFromCurveHnd = IFCInstanceExporter.CreateConnectionSurfaceGeometry(file, surfOnRelatingElement, null);
            return extrudedSurfFromCurveHnd;
        }
    }
}
