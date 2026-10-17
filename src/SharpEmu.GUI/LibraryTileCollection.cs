// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace SharpEmu.GUI;

/// <summary>
/// Read-only view of the visible games with one persistent action tile at
/// the end. Game collection changes keep their original indices, so Avalonia
/// can update and virtualize the rail without rebuilding every item.
/// </summary>
public sealed class LibraryTileCollection : IReadOnlyList<LibraryTile>, IList, INotifyCollectionChanged
{
    private readonly ObservableCollection<GameEntry> _games;
    private bool _includeAddFolderTile = true;

    public LibraryTileCollection(ObservableCollection<GameEntry> games)
    {
        _games = games;
        _games.CollectionChanged += OnGamesCollectionChanged;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public bool IncludeAddFolderTile
    {
        get => _includeAddFolderTile;
        set
        {
            if (_includeAddFolderTile == value)
            {
                return;
            }

            _includeAddFolderTile = value;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public int Count => _games.Count + (_includeAddFolderTile ? 1 : 0);

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public LibraryTile this[int index]
    {
        get
        {
            if ((uint)index < (uint)_games.Count)
            {
                return _games[index];
            }

            if (_includeAddFolderTile && index == _games.Count)
            {
                return AddFolderTile.Instance;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<LibraryTile> GetEnumerator()
    {
        foreach (var game in _games)
        {
            yield return game;
        }

        if (_includeAddFolderTile)
        {
            yield return AddFolderTile.Instance;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.IndexOf(object? value) => value switch
    {
        GameEntry game => _games.IndexOf(game),
        AddFolderTile when _includeAddFolderTile => _games.Count,
        _ => -1,
    };

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (var tile in this)
        {
            array.SetValue(tile, index++);
        }
    }

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        CollectionChanged?.Invoke(this, args);
}
