using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autocaster.Core;

namespace Autocaster.App;

internal sealed class ActionLayout(ScenarioProfile profile)
{
    private sealed record Block(ActionGroup? Group, List<ScenarioAction> Actions);
    private readonly List<Block> _blocks = Read(profile);

    private static List<Block> Read(ScenarioProfile profile)
    {
        var blocks = new List<Block>();
        int index = 0;
        foreach (var group in profile.ActionGroups)
        {
            while (index < group.Start)
                blocks.Add(new(null, [profile.Actions[index++]]));
            blocks.Add(new(group, profile.Actions.Skip(index).Take(group.Count).ToList()));
            index += group.Count;
        }
        while (index < profile.Actions.Length)
            blocks.Add(new(null, [profile.Actions[index++]]));
        return blocks;
    }

    internal ScenarioProfile ToProfile()
    {
        var actions = ImmutableArray.CreateBuilder<ScenarioAction>();
        var groups = ImmutableArray.CreateBuilder<ActionGroup>();
        foreach (var block in _blocks)
        {
            if (block.Group is { } group)
                groups.Add(group with { Start = actions.Count, Count = block.Actions.Count });
            actions.AddRange(block.Actions);
        }
        return profile with
        {
            Actions = actions.SequenceEqual(profile.Actions) ? profile.Actions : actions.ToImmutable(),
            ActionGroups = groups.SequenceEqual(profile.ActionGroups) ? profile.ActionGroups : groups.ToImmutable()
        };
    }

    internal void Insert(Guid? after, IEnumerable<ScenarioAction> actions)
    {
        var block = Find(after);
        if (block?.Group is not null)
        {
            int index = block.Group.Id == after ? block.Actions.Count : block.Actions.FindIndex(action => action.Id == after) + 1;
            block.Actions.InsertRange(index, actions);
            return;
        }
        _blocks.InsertRange(block is null ? _blocks.Count : _blocks.IndexOf(block) + 1,
            actions.Select(action => new Block(null, [action])));
    }

    internal void Delete(IReadOnlySet<Guid> ids)
    {
        foreach (var block in _blocks)
            block.Actions.RemoveAll(action => ids.Contains(action.Id));
        _blocks.RemoveAll(block => block.Group is null && block.Actions.Count == 0);
    }

    internal Guid AddGroup(Guid? after, IReadOnlySet<Guid>? ids)
    {
        var names = _blocks.Select(block => block.Group?.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int number = _blocks.Count(block => block.Group is not null) + 1;
        while (names.Contains($"Group {number}"))
            number++;
        var group = new ActionGroup(Guid.NewGuid(), $"Group {number}", 0, 0, (number - 1) % 4);
        var selected = ids is { Count: > 0 } ? profile.Actions.Where(action => ids.Contains(action.Id)).ToList() : [];
        var created = new Block(group, selected);
        if (selected.Count == 0)
        {
            var afterBlock = Find(after);
            _blocks.Insert(afterBlock is null ? _blocks.Count : _blocks.IndexOf(afterBlock) + 1, created);
        }
        else
        {
            var updated = new List<Block>();
            var first = Find(selected[0].Id);
            foreach (var block in _blocks)
            {
                if (ReferenceEquals(block, first))
                {
                    int split = block.Actions.FindIndex(action => action.Id == selected[0].Id);
                    if (split > 0)
                        updated.Add(new(block.Group, block.Actions.GetRange(0, split)));
                    updated.Add(created);
                    var tail = block.Actions.Skip(split).Where(action => !ids!.Contains(action.Id)).ToList();
                    if (tail.Count > 0)
                        updated.Add(new(split > 0 && block.Group is { } parent ? parent with { Id = Guid.NewGuid() } : block.Group, tail));
                }
                else if (block.Actions.RemoveAll(action => ids!.Contains(action.Id)) == 0 || block.Actions.Count > 0)
                    updated.Add(block);
            }
            _blocks.Clear();
            _blocks.AddRange(updated);
        }
        return group.Id;
    }

    internal void Rename(Guid id, string name)
    {
        var block = Find(id);
        if (block?.Group is { } group)
            _blocks[_blocks.IndexOf(block)] = block with { Group = group with { Name = name.Trim() } };
    }

    internal void Ungroup(Guid id)
    {
        var block = Find(id);
        if (block?.Group is null)
            return;
        int index = _blocks.IndexOf(block);
        _blocks.RemoveAt(index);
        _blocks.InsertRange(index, block.Actions.Select(action => new Block(null, [action])));
    }

    internal void Move(IReadOnlySet<Guid> ids, Guid target, bool after, bool outside)
    {
        var destination = Find(target);
        if (ids.Contains(target) || destination is null)
            return;
        var moving = profile.Actions.Where(action => ids.Contains(action.Id)).ToArray();
        var sources = _blocks.Where(block => block.Actions.Any(action => ids.Contains(action.Id))).ToArray();
        if (destination.Group is not null && !outside)
        {
            int index = destination.Group.Id == target ? destination.Actions.Count :
                destination.Actions.FindIndex(action => action.Id == target) + (after ? 1 : 0);
            index -= destination.Actions.Take(index).Count(action => ids.Contains(action.Id));
            foreach (var source in sources)
                source.Actions.RemoveAll(action => ids.Contains(action.Id));
            destination.Actions.InsertRange(index, moving);
        }
        else
        {
            foreach (var source in sources)
                source.Actions.RemoveAll(action => ids.Contains(action.Id));
            int index = _blocks.IndexOf(destination) + (after ? 1 : 0);
            _blocks.InsertRange(index, moving.Select(action => new Block(null, [action])));
        }
        _blocks.RemoveAll(block => block.Actions.Count == 0 && sources.Contains(block));
    }

    private Block? Find(Guid? id) => id is Guid value
        ? _blocks.Find(block => block.Group?.Id == value || block.Actions.Any(action => action.Id == value)) : null;

}
