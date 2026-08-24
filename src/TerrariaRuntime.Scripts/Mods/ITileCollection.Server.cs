/*
Copyright (C) 2020 DeathCradle

This file is part of Open Terraria API v3 (OTAPI)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

using TerrariaRuntime.PatchEngine;
using TerrariaRuntime.PatchEngine.Relinking;
using Mono.Cecil;
using System;

/// <summary>
/// @doc Transforms Terraria.Tile[] types to the TerrariaRuntime patch-engine collection
/// </summary>
[Patch(PatchStage.PreMerge, "Implementing Tile collections")]
[MonoMod.MonoModIgnore]
void ITileCollection(AssemblyPatcher modder, IRelinkProvider relinkProvider)
{
    var tile = modder.GetDefinition<Terraria.Tile>();
    tile.RelinkAsCollection(
        modder,
        typeof(TerrariaRuntime.ICollection<>),
        typeof(TerrariaRuntime.DefaultCollection<>));
}

[Patch(PatchStage.PostMerge, "Normalizing merged tile collection signatures")]
[MonoMod.MonoModIgnore]
void NormalizeTileCollectionSignatures(AssemblyPatcher modder)
{
    const string collectionTypeName = "TerrariaRuntime.ICollection`1";
    var collectionDefinition = modder.Module.GetType(collectionTypeName)
        ?? throw new TypeLoadException(collectionTypeName);

    TypeReference Normalize(TypeReference type)
    {
        if (type is not GenericInstanceType generic || generic.ElementType.FullName != collectionTypeName)
            return type;

        var normalized = new GenericInstanceType(collectionDefinition);
        foreach (var argument in generic.GenericArguments)
            normalized.GenericArguments.Add(argument);
        return normalized;
    }

    foreach (var type in modder.Module.GetTypes())
    {
        foreach (var field in type.Fields)
            field.FieldType = Normalize(field.FieldType);

        foreach (var property in type.Properties)
            property.PropertyType = Normalize(property.PropertyType);

        foreach (var method in type.Methods)
        {
            method.ReturnType = Normalize(method.ReturnType);
            foreach (var parameter in method.Parameters)
                parameter.ParameterType = Normalize(parameter.ParameterType);
        }
    }
}

namespace TerrariaRuntime
{
    public interface ICollection<TItem>
    {
        TItem this[int x, int y] { get; set; }
        int Width { get; }
        int Height { get; }
    }

    public class DefaultCollection<TItem> : ICollection<TItem>
    {
        protected TItem[,]? _items;

        public int Width { get; set; }
        public int Height { get; set; }

        public DefaultCollection(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public virtual TItem this[int x, int y]
        {
            get
            {
                _items ??= new TItem[Width, Height];
                return _items[x, y];
            }
            set
            {
                _items ??= new TItem[Width, Height];
                _items[x, y] = value;
            }
        }

        public delegate ICollection<TItem> CreateCollectionHandler(int width, int height, string source);
        public static event CreateCollectionHandler? OnCreateCollection;

        public static ICollection<TItem> CreateCollection(int width, int height, string source)
        {
            var collection = OnCreateCollection?.Invoke(width, height, source)
                ?? new DefaultCollection<TItem>(width, height);
            System.Diagnostics.Debug.WriteLine(
                $"Created new {collection.Width}x{collection.Height} {collection.GetType().Name} for source: {source}");
            return collection;
        }
    }
}
