using Grasshopper;
using Grasshopper.Kernel;
using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace hopperdraw
{
    public class HopperDrawInfo : GH_AssemblyInfo
    {
        public override string Name => "hopperdraw";

        public override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var stream = assembly.GetManifestResourceStream("hopperborder.icon.png");
                    if (stream != null)
                    {
                        return new Bitmap(stream);
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        public override string Description => "Canvas drawing plugin for Grasshopper. Press D twice to enter drawing mode; Escape exits.";

        public override Guid Id => new Guid("3e405a88-6607-40ab-bb81-added3ce85ce");

        public override string AuthorName => "AEC Tooling";

        public override string AuthorContact => "";

        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}
