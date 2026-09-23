using System.Text.Json;
using ChronoCore;

namespace ChronoWeb;

/// <summary>把领域案例包物化为仓库自带的离线文件（非通用表格）。</summary>
public static class CasePackageTool
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string CaseJson => JsonSerializer.Serialize(CaseBuilder.Build(), Options);

    public static void WriteToDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "core-chronicle-7b.case.json"), CaseJson);
        File.WriteAllText(Path.Combine(dir, "README.md"), CaseReadme);
    }

    public const string CaseReadme = """
# 离线案例包：岩芯年代尺 core-chronicle-7b

专门面向“岩芯年代尺：深度锚点、沉积速率与多候选模型谱系”的领域材料，不是通用表格。

- `core-chronicle-7b.case.json`：完整案例（深度基准、连续分段、取心缺口与不整合、
  放射性年龄分布（含经验/正态/学生-t 长尾）、储层偏移、火山灰/浊积事件层、全部排除决定、
  参数版本 v1→v3、叙事步骤 step-1..5、七个反例情景）。
- 初始版本即完整呈现“深度基准、分段缺口、放射性年龄分布、储层偏移、事件层相关和所有排除决定”。
- 随后安排：一次参数修订、一次只影响局部依赖的源数据改动（an-07 仅 iv-2）、一次互相冲突的人工决定。
- 反例重放实际经过：深度重复、不整合、倒置层段、并列锚点、年龄分布长尾、局部重算、并发冻结。
- 所有领域量携带单位或坐标基准；导入保留原始表示，派生层才规范化；无法转换的值留在原位置并附诊断。
""";
}
