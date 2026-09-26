using DualNet.Core.Services;

namespace DualNet.Tests;

[TestClass]
public sealed class PowerShellRunnerTests
{
    [TestMethod]
    public void CliXmlProgressIsRemovedAndOnlyReadableErrorRemains()
    {
        const string cliXml = """
        #< CLIXML
        <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04">
          <Obj S="progress" RefId="0"><MS><PR N="Record"><AV>正在准备首次使用模块。</AV></PR></MS></Obj>
          <S S="Error">Get-NetIPAddress : 找不到指定的本机地址。_x000D__x000A_请检查地址后重试。</S>
        </Objs>
        """;

        var result = PowerShellRunner.NormalizeError(cliXml);

        Assert.IsFalse(result.Contains("CLIXML", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("正在准备首次使用模块", StringComparison.Ordinal));
        StringAssert.Contains(result, "Get-NetIPAddress : 找不到指定的本机地址。");
        StringAssert.Contains(result, "请检查地址后重试。");
    }
}
