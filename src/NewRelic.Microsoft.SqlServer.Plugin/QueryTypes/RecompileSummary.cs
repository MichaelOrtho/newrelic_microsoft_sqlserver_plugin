using NewRelic.Microsoft.SqlServer.Plugin.Core;

namespace NewRelic.Microsoft.SqlServer.Plugin.QueryTypes
{
    [Query("RecompileSummary.sql", "Component/Recompiles/{DatabaseName}", QueryName = "Recompile Summary", Enabled = true)]
    internal class RecompileSummary : RecompileQueryBase
    {
        public int SingleUseObjects { get; set; }
        public int MultipleUseObjects { get; set; }
        public decimal SingleUsePercent { get; set; }
    }
}
