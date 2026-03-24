using Grasshopper;
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace hopperdraw
{
    public class HopperDrawInfo : GH_AssemblyInfo
    {
        public override string Name => "hopperdraw";

        public override Bitmap Icon => null;

        public override string Description => "Canvas drawing plugin for Grasshopper";

        public override Guid Id => new Guid("3e405a88-6607-40ab-bb81-added3ce85ce");

        public override string AuthorName => "AEC Tooling";

        public override string AuthorContact => "";

        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}