using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using Xunit;

namespace IGW.Tests;

public sealed class MesListenerSecretTests
{
    [Fact]
    public void Editable_project_redacts_listener_credentials_and_restores_them_on_MES_save()
    {
        var project = new IgwProject();
        project.MesUpload.Tasks.Add(new() { Id = "listener", Listener = new() { Headers = new()
            { ["Authorization"] = "Bearer private", ["Cookie"] = "session=private", ["X-Api-Key"] = "private-key", ["X-Station"] = "OP010" } } });
        var copy = ProjectConfigurationRedactor.Create(project);
        Assert.Equal(ProjectConfigurationRedactor.UnchangedSecret, copy.MesUpload.Tasks[0].Listener.Headers["Authorization"]);
        Assert.Equal(ProjectConfigurationRedactor.UnchangedSecret, copy.MesUpload.Tasks[0].Listener.Headers["Cookie"]);
        Assert.Equal(ProjectConfigurationRedactor.UnchangedSecret, copy.MesUpload.Tasks[0].Listener.Headers["X-Api-Key"]);
        Assert.Equal("OP010", copy.MesUpload.Tasks[0].Listener.Headers["X-Station"]);
        ProjectConfigurationRedactor.MergeMesSecrets(project.MesUpload, copy.MesUpload);
        Assert.Equal("Bearer private", copy.MesUpload.Tasks[0].Listener.Headers["Authorization"]);
    }
}
