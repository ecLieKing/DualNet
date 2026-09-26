namespace DualNet.Core.Services;

public interface IPowerShellRunner
{
    Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default);
}
