using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Geometry;

namespace hopperdraw
{
    public static class PointExtensions
    {
        public static PointF ToPointF(this Point3d pt) => new PointF((float)pt.X, (float)pt.Y);

        public static Point3d ToPoint3d(this PointF pt) => new Point3d(pt.X, pt.Y, 0);

        public static PointF[] ToPointFArray(this Point3d[] pts) =>
            Array.ConvertAll(pts, p => new PointF((float)p.X, (float)p.Y));

        public static PointF[] ToPointFArray(this List<Point3d> pts) =>
            pts.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
    }
}
