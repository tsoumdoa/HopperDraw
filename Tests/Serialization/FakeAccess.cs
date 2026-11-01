using System;
using System.Collections.Generic;
using System.Globalization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
internal sealed class FakeAccess : IGH_DataAccess
    {
        private readonly object[] _values;
        internal FakeAccess(params object[] values) { _values = values; }
        public bool GetData<T>(int index, ref T destination)
        {
            if (index >= _values.Length || _values[index] == null) return false;
            destination = (T)Convert.ChangeType(_values[index], typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        public int Iteration => 0;
        public void IncrementIteration() { }
        public void DisableGapLogic() { }
        public void DisableGapLogic(int paramIndex) { }
        public GH_Path ParameterTargetPath(int paramIndex) => new GH_Path(0);
        public int ParameterTargetIndex(int paramIndex) => 0;
        public void AbortComponentSolution() { }
        public List<T> Util_RemoveNullRefs<T>(List<T> L) => L;
        public int Util_CountNullRefs<T>(List<T> L) => 0;
        public int Util_CountNonNullRefs<T>(List<T> L) => L.Count;
        public bool Util_EnsureNonNullCount<T>(List<T> L, int N) => true;
        public int Util_FirstNonNullItem<T>(List<T> L) => 0;
        public bool SetData(int paramIndex, object data) => false;
        public bool SetData(int paramIndex, object data, int itemIndexOverride) => false;
        public bool SetData(string paramName, object data) => false;
        public bool SetDataList(int paramIndex, System.Collections.IEnumerable data) => false;
        public bool SetDataList(int paramIndex, System.Collections.IEnumerable data, int listIndexOverride) => false;
        public bool SetDataList(string paramName, System.Collections.IEnumerable data) => false;
        public bool SetDataTree(int paramIndex, IGH_DataTree tree) => false;
        public bool SetDataTree(int paramIndex, IGH_Structure tree) => false;
        public bool BlitData<Q>(int paramIndex, GH_Structure<Q> tree, bool overwrite) where Q : Grasshopper.Kernel.Types.IGH_Goo => false;
        public bool GetData<T>(string name, ref T destination) => false;
        public bool GetDataList<T>(int index, List<T> list) => false;
        public bool GetDataList<T>(string name, List<T> list) => false;
        public bool GetDataTree<T>(int index, out GH_Structure<T> tree) where T : Grasshopper.Kernel.Types.IGH_Goo { tree = null; return false; }
        public bool GetDataTree<T>(string name, out GH_Structure<T> tree) where T : Grasshopper.Kernel.Types.IGH_Goo { tree = null; return false; }
    }
