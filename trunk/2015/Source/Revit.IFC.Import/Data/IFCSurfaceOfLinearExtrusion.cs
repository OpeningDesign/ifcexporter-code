﻿//
// Revit IFC Import library: this library works with Autodesk(R) Revit(R) to import IFC files.
// Copyright (C) 2013  Autodesk, Inc.
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
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Revit.IFC.Common.Utility;
using Revit.IFC.Common.Enums;
using Revit.IFC.Import.Enums;
using Revit.IFC.Import.Geometry;
using Revit.IFC.Import.Utility;

namespace Revit.IFC.Import.Data
{
    public class IFCSurfaceOfLinearExtrusion : IFCSweptSurface
    {
        XYZ m_ExtrudedDirection = null;

        double m_Depth = 0.0;

        public XYZ ExtrudedDirection
        {
            get { return m_ExtrudedDirection; }
            protected set { m_ExtrudedDirection = value; }
        }

        public double Depth
        {
            get { return m_Depth; }
            protected set { m_Depth = value; }
        }

        /// <summary>
        /// Get the local surface transform at a given point on the surface.
        /// </summary>
        /// <param name="pointOnSurface">The point.</param>
        /// <returns>The transform.</returns>
        /// <remarks>This does not include the translation component.</remarks>
        public override Transform GetTransformAtPoint(XYZ pointOnSurface)
        {
            if (!(SweptCurve is IFCSimpleProfile))
            {
                // LOG: ERROR: warn that we only support simple profiles.
                return null;
            }

            CurveLoop outerCurveLoop = (SweptCurve as IFCSimpleProfile).OuterCurve;
            if (outerCurveLoop == null || outerCurveLoop.Count() != 1)
            {
                // LOG: ERROR
                return null;
            }

            Curve outerCurve = outerCurveLoop.First();
            if (outerCurve == null)
            {
                // LOG: ERROR
                return null;
            }

            IntersectionResult result = outerCurve.Project(pointOnSurface);
            if (result == null)
            {
                // LOG: ERROR
                return null;
            }

            double parameter = result.Parameter;

            Transform atPoint = outerCurve.ComputeDerivatives(parameter, false);
            atPoint.set_Basis(0, atPoint.BasisX.Normalize());
            atPoint.set_Basis(1, atPoint.BasisY.Normalize());
            atPoint.set_Basis(2, atPoint.BasisZ.Normalize());
            atPoint.Origin = pointOnSurface;

            return atPoint;
        }

        protected IFCSurfaceOfLinearExtrusion()
        {
        }

        override protected void Process(IFCAnyHandle ifcSurface)
        {
            base.Process(ifcSurface);

            IFCAnyHandle extrudedDirection = IFCImportHandleUtil.GetRequiredInstanceAttribute(ifcSurface, "ExtrudedDirection", true);
            ExtrudedDirection = IFCPoint.ProcessNormalizedIFCDirection(extrudedDirection);

            bool found = false;
            Depth = IFCImportHandleUtil.GetRequiredScaledLengthAttribute(ifcSurface, "Depth", out found);
            if (!found)
                Importer.TheLog.LogError(Id, "IfcSurfaceOfLinearExtrusion has no height, ignoring.", true);
        }

        protected IFCSurfaceOfLinearExtrusion(IFCAnyHandle surfaceOfLinearExtrusion)
        {
            Process(surfaceOfLinearExtrusion);
        }

        /// <summary>
        /// Create an IFCSurfaceOfLinearExtrusion object from a handle of type IfcSurfaceOfLinearExtrusion.
        /// </summary>
        /// <param name="ifcSurfaceOfLinearExtrusion">The IFC handle.</param>
        /// <returns>The IFCSurfaceOfLinearExtrusion object.</returns>
        public static IFCSurfaceOfLinearExtrusion ProcessIFCSurfaceOfLinearExtrusion(IFCAnyHandle ifcSurfaceOfLinearExtrusion)
        {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(ifcSurfaceOfLinearExtrusion))
            {
                Importer.TheLog.LogNullError(IFCEntityType.IfcSurfaceOfLinearExtrusion);
                return null;
            }

            IFCEntity surfaceOfLinearExtrusion;
            if (!IFCImportFile.TheFile.EntityMap.TryGetValue(ifcSurfaceOfLinearExtrusion.StepId, out surfaceOfLinearExtrusion))
                surfaceOfLinearExtrusion = new IFCSurfaceOfLinearExtrusion(ifcSurfaceOfLinearExtrusion);

            return surfaceOfLinearExtrusion as IFCSurfaceOfLinearExtrusion;
        }
    }
}
