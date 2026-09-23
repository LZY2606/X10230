using Chrono.Storage.CasePack;

namespace Chrono.Web.Api;

public sealed class ReplayReportHolder
{
    public ReplayReport? LastReport { get; set; }
}
