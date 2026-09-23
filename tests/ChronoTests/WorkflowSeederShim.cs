using ChronoCore;
using ChronoStorage;
using ChronoWeb;

namespace ChronoTests;

// 测试直接复用 Web 层的完整叙事播种器（覆盖导入/推导/复核工作流）
internal sealed class WorkflowSeederShim
{
    private readonly ChronoRepository _repo;
    public WorkflowSeederShim(ChronoRepository repo) { _repo = repo; }
    public SeedingSummary Seed() => new WorkflowSeeder(_repo).Seed();
}
