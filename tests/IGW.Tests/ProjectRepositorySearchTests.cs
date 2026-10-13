using IGW.Abstractions;
using IGW.Core;
using IGW.Northbound.RestApi;
using IGW.Persistence.Sqlite;
using Xunit;

namespace IGW.Tests;

public sealed class ProjectRepositorySearchTests
{
    [Fact]
    public async Task Selectors_query_only_indexed_scopes_and_preserve_paths_access_lengths_arrays_and_cursor()
    {
        await using var fixture = await SearchFixture.CreateAsync(ProjectWithHundredThousandDuplicateLeaves());
        var repository = new NoRuntimeLoadRepository(fixture.Repository);

        ProjectParameterOptionsResponse writable = await ProjectParameterSearch.SearchAsync(
            repository, "Shared49999", "Factory.PLC.AreaA", "tag", "write", 20, default, scopeOnly: false);
        Assert.NotEmpty(repository.SearchQueries);
        Assert.All(repository.SearchQueries, query => Assert.False(query.CountTotal));
        ProjectParameterOptionsResponse scopedWrite = await ProjectParameterSearch.SearchAsync(
            repository, "Shared49999", "Factory.PLC.AreaA", "tag", "write", 20, default);
        Assert.Empty(scopedWrite.Items);
        repository.SearchQueries.Clear();
        ProjectParameterOptionsResponse exactCurrent = await ProjectParameterSearch.SearchAsync(
            repository, "Factory.PLC.AreaA.Text", "Factory.PLC.AreaA", "tag", "read", 20, default, scopeOnly: false);
        Assert.Equal("Factory.PLC.AreaA.Text", Assert.Single(exactCurrent.Items).FullPath);
        Assert.Single(repository.SearchQueries);
        ProjectTagScopeResponse scope = await ProjectTagScopeApi.SearchAsync(
            repository, "\"project-1\"", "realtime-1", "Factory.PLC.AreaA", null, 2, default);
        MesRangeOptionsResponse ranges = await MesRangeOptionsApi.SearchAsync(
            repository, "Factory.PLC.AreaA", "ArrayRoot", null, 20, default);

        ProjectParameterOption option = Assert.Single(writable.Items);
        Assert.Equal("Factory.PLC.AreaB.Shared49999", option.FullPath);
        Assert.Equal("DB2.DBW99998", option.Address);
        Assert.Equal("Factory.PLC.AreaA.ArrayRoot", scope.Definitions[0].Id);
        Assert.Equal("ReadWrite", scope.Definitions[0].Access);
        Assert.Equal([100_000], scope.Definitions[0].Array!.Dimensions);
        Assert.Equal(64, scope.Definitions[1].StringLength);
        Assert.NotNull(scope.NextCursor);
        MesRangeOptionDto array = Assert.Single(ranges.Items);
        Assert.Equal("Factory.PLC.AreaA.ArrayRoot", array.SourcePath);
        Assert.Equal("Array", array.Scope);
        Assert.Equal(100_000, array.ExistingElementCount);
        Assert.Equal(0, repository.RuntimeLoadCalls);
    }

    [Fact]
    public async Task Scoped_block_keeps_the_declared_order_so_generated_fields_follow_the_db()
    {
        // 与 TIA 里的声明顺序一致(不是字母顺序)：MESFlag、PLCFlag、TrayCode…，子组里同样
        var project = new IgwProject
        {
            Channels = [new ChannelDef { Name = "L", Devices = [new DeviceDef { Name = "P", Groups = [new TagGroupDef
            {
                Name = "ST430",
                Tags = [new TagDef { Name = "MESFlag", Address = "DB1.DBW0" }, new TagDef { Name = "PLCFlag", Address = "DB1.DBW2" },
                    new TagDef { Name = "TrayCode", Address = "DB1.DBW4" }, new TagDef { Name = "Batch1", Address = "DB1.DBW6" }],
                SubGroups = [new TagGroupDef { Name = "Zeta", Tags = [new TagDef { Name = "Z2", Address = "DB1.DBW8" }, new TagDef { Name = "A1", Address = "DB1.DBW10" }] },
                    new TagGroupDef { Name = "Alpha", Tags = [new TagDef { Name = "X", Address = "DB1.DBW12" }] }],
            }] }] }],
        };
        await using var fixture = await SearchFixture.CreateAsync(project);

        IgwProject scoped = await MesRangeOptionsApi.LoadScopedProjectAsync(fixture.Repository, "L.P.ST430", default);

        var block = scoped.Channels[0].Devices[0].Groups.Single();
        Assert.Equal(["MESFlag", "PLCFlag", "TrayCode", "Batch1"], block.Tags.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Zeta", "Alpha"], block.SubGroups.Select(group => group.Name).ToArray());
        Assert.Equal(["Z2", "A1"], block.SubGroups[0].Tags.Select(tag => tag.Name).ToArray());
    }

    private static IgwProject ProjectWithHundredThousandDuplicateLeaves()
    {
        var areaA = new TagGroupDef
        {
            Id = "group-area-a",
            Name = "AreaA",
            Tags =
            [
                new TagDef
                {
                    Id = "array-root",
                    Name = "ArrayRoot",
                    Address = "DB1.DBW0",
                    DataType = IgwDataType.Int16,
                    Access = TagAccess.ReadWrite,
                    IsArray = true,
                    Array = new ArrayDef
                    {
                        ElementType = IgwDataType.Int16,
                        Dimensions = [100_000],
                        LowerBounds = [0],
                        ElementStrideBytes = 2,
                    },
                },
                new TagDef
                {
                    Id = "string-root",
                    Name = "Text",
                    Address = "DB1.DBB200010",
                    DataType = IgwDataType.String,
                    Access = TagAccess.Read,
                    StringLength = 64,
                },
            ],
        };
        var areaB = new TagGroupDef { Id = "group-area-b", Name = "AreaB" };
        for (int index = 0; index < 50_000; index++)
        {
            areaA.Tags.Add(new TagDef
            {
                Id = $"a-{index:D5}",
                Name = $"Shared{index:D5}",
                Address = $"DB1.DBW{index * 2 + 200_100}",
                Access = TagAccess.Read,
            });
            areaB.Tags.Add(new TagDef
            {
                Id = $"b-{index:D5}",
                Name = $"Shared{index:D5}",
                Address = $"DB2.DBW{index * 2}",
                Access = TagAccess.ReadWrite,
            });
        }
        return new IgwProject
        {
            Name = "十万点搜索",
            Channels =
            [
                new ChannelDef
                {
                    Id = "channel-factory",
                    Name = "Factory",
                    DriverId = "Simulator",
                    Devices =
                    [
                        new DeviceDef
                        {
                            Id = "device-plc",
                            Name = "PLC",
                            Groups = [areaA, areaB],
                        },
                    ],
                },
            ],
        };
    }

    private sealed class NoRuntimeLoadRepository(IProjectRepository inner) : IProjectRepository
    {
        public int RuntimeLoadCalls { get; private set; }
        public List<ProjectNodeSearchQuery> SearchQueries { get; } = [];
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task<ProjectRevision> GetRevisionAsync(CancellationToken ct) => inner.GetRevisionAsync(ct);
        public Task<ProjectTreeNode> GetRootAsync(CancellationToken ct) => inner.GetRootAsync(ct);
        public Task<IReadOnlyList<ProjectTreeNode>> GetChildrenAsync(ProjectNodeRef parent, CancellationToken ct) => inner.GetChildrenAsync(parent, ct);
        public Task<ProjectPage<ProjectTagRow>> GetTagsAsync(ProjectTagQuery query, CancellationToken ct) => inner.GetTagsAsync(query, ct);
        public Task<ProjectPage<ProjectMesTaskRow>> GetMesTasksAsync(ProjectMesTaskQuery query, CancellationToken ct) => inner.GetMesTasksAsync(query, ct);
        public Task<ProjectPage<ProjectNodeSearchRow>> SearchNodesAsync(ProjectNodeSearchQuery query, CancellationToken ct)
        {
            SearchQueries.Add(query);
            return inner.SearchNodesAsync(query, ct);
        }
        public Task<string> GetEntityDocumentAsync(ProjectNodeRef entity, CancellationToken ct) => inner.GetEntityDocumentAsync(entity, ct);
        public Task<ProjectMutationResult> ExecuteAsync(ProjectMutation mutation, ProjectRevision expected, CancellationToken ct) => inner.ExecuteAsync(mutation, expected, ct);
        public Task<IgwProject> LoadRuntimeProjectAsync(CancellationToken ct)
        {
            RuntimeLoadCalls++;
            throw new Xunit.Sdk.XunitException("Selector must not load the complete runtime project.");
        }
        public Task<ProjectRevision> ReplaceFromSnapshotAsync(IgwProject project, ProjectRevision expected, CancellationToken ct) => inner.ReplaceFromSnapshotAsync(project, expected, ct);
        public Task<IProjectReadSnapshot> OpenReadSnapshotAsync(CancellationToken ct) => inner.OpenReadSnapshotAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SearchFixture : IAsyncDisposable
    {
        private readonly string _directory;
        public SqliteProjectRepository Repository { get; }

        private SearchFixture(string directory, SqliteProjectRepository repository)
        {
            _directory = directory;
            Repository = repository;
        }

        public static async Task<SearchFixture> CreateAsync(IgwProject project)
        {
            string directory = Path.Combine(Path.GetTempPath(), "igw-project-search-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var repository = new SqliteProjectRepository(
                new ProjectDatabase(Path.Combine(directory, "project.db")),
                new ProjectSecretVault(new InstallationSecretProtector(Path.Combine(directory, "igw.masterkey"))));
            await repository.InitializeAsync(default);
            await repository.ReplaceFromSnapshotAsync(project, new ProjectRevision(0), default);
            return new SearchFixture(directory, repository);
        }

        public async ValueTask DisposeAsync()
        {
            await Repository.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
