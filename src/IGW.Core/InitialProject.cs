using IGW.Abstractions;

namespace IGW.Core;

/// <summary>正式安装首次运行使用的空工程；演示数据只能由测试或示例工具显式导入。</summary>
public static class InitialProject
{
    public static IgwProject Create() => new() { Name = "项目" };
}
