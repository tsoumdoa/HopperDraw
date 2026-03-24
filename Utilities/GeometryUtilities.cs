using System;
using System.Drawing;
using Rhino.Geometry;

namespace hopperborder
{
    public static class GeometryUtilities
    {
        public static float Distance(PointF a, PointF b) =>
            (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        public static float DistanceToSegment(PointF pt, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f) return Distance(pt, a);
            float t = Math.Max(0, Math.Min(1, ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lengthSq));
            return Distance(pt, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        public static Point3d SnapAngle(Point3d start, Point3d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0)
            {
                double angle = Math.Atan2(dy, dx);
                double snapAngle = Math.Round(angle / (Math.PI / 4.0)) * (Math.PI / 4.0);
                return new Point3d(
                    start.X + length * Math.Cos(snapAngle),
                    start.Y + length * Math.Sin(snapAngle),
                    0);
            }
            return end;
        }

        public static Point3d SnapToAspectRatio(Point3d corner, Point3d newPos, double aspectRatio)
        {
            double w = newPos.X - corner.X;
            double h = newPos.Y - corner.Y;
            double absW = Math.Abs(w);
            double absH = Math.Abs(h);
            double signW = Math.Sign(w);
            double signH = Math.Sign(h);

            if (absW > 2 && absH > 2)
            {
                if (absW > absH * aspectRatio)
                {
                    absH = absW / aspectRatio;
                }
                else
                {
                    absW = absH * aspectRatio;
                }
                return new Point3d(corner.X + signW * absW, corner.Y + signH * absH, 0);
            }
            return newPos;
        }
    }
}
