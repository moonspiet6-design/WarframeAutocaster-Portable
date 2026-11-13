using Autocaster.Core;
using Autocaster.Infrastructure;

namespace Autocaster.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task BundledMacrosImportAndCompile()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "bundled");
        var (profiles, errors) = await new ProfileService(folder).LoadAll();
        Assert.Empty(errors);
        Assert.Equal(new[] { "nidus aol", "nidus ol" }, profiles.Select(profile => profile.Name));
        var compiler = new ScenarioCompiler(new());
        foreach (var profile in profiles)
        {
            Assert.True(profile.Macro?.Step);
            Assert.Null(profile.Index);
            Assert.NotEmpty(compiler.Compile(new MacroSteps().Plan(profile)).Events);
        }
    }

    [Fact]
    public async Task OpensLegacyGroupsAsFlatActionsAndRejectsExternalEdits()
    {
        string root = Path.Combine(Path.GetTempPath(), $"AutocasterTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profile = CompilerTests.Profile(new ScenarioAction
            {
                Kind = ActionKind.Repeat,
                Children = [new() { Kind = ActionKind.Wait, Delay = Timing.Range(200, 500, 100) }]
            });
            var store = new ProfileService(root);
            await store.Save(profile);
            var loaded = await new ProfileService(root).LoadAll();
            Assert.Empty(loaded.Errors);
            var item = Assert.Single(loaded.Profiles);
            Assert.Equal(profile.Id, item.Id);
            Assert.Equal(2, item.Actions.Length);
            Assert.All(item.Actions, action =>
            {
                Assert.Equal(ActionKind.Wait, action.Kind);
                Assert.Equal(Timing.Range(200, 500, 100), action.Delay);
            });
            Assert.Equal(profile.Actions[0].Children[0].Id, item.Actions[0].Id);
            Assert.NotEqual(item.Actions[0].Id, item.Actions[1].Id);
            string file = Assert.Single(Directory.GetFiles(root));
            await File.AppendAllTextAsync(file, " ");
            await Assert.ThrowsAsync<IOException>(() => store.Save(profile with { Name = "new" }));
            await Assert.ThrowsAsync<IOException>(() => store.Delete(profile.Id));
            Assert.True(File.Exists(file));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root))
                File.Delete(file);
            Directory.Delete(root);
        }
    }
}
