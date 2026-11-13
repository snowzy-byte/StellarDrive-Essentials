using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace StellarDriveEssentials;
internal sealed class ObjGeometry
{
    internal readonly List<Vector3> Vertices = new List<Vector3>();
    internal readonly List<Vector2> Uvs = new List<Vector2>();
    internal readonly List<Vector3> Normals = new List<Vector3>();
    internal readonly List<List<int>> Submeshes = new List<List<int>>();
    internal readonly List<string> Materials = new List<string>();
    internal bool HasNormals = true;
}

internal static class ObjModel
{
    internal static Mesh FromObj(string source, string name)
    {
        ObjGeometry data = Parse(source);
        var mesh = new Mesh
        {
            name = name,
            hideFlags = HideFlags.DontSave
        };
        if (data.Vertices.Count > 65000)
            mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(data.Vertices);
        mesh.SetUVs(0, data.Uvs);
        if (data.HasNormals)
            mesh.SetNormals(data.Normals);
        mesh.subMeshCount = data.Submeshes.Count;
        for (int i = 0; i < data.Submeshes.Count; i++)
            mesh.SetTriangles(data.Submeshes[i], i);
        if (!data.HasNormals)
            mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    internal static ObjGeometry Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException("Empty Essentials OBJ model.");
        var data = new ObjGeometry();
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var corners = new Dictionary<(int, int, int), int>();
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        List<int> triangles = null;
        using (var reader = new StringReader(source))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                int comment = line.IndexOf('#');
                if (comment >= 0)
                    line = line.Substring(0, comment);
                string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                    continue;
                switch (words[0])
                {
                    case "v":
                        positions.Add(new Vector3(0f - Number(words, 1), Number(words, 2), Number(words, 3)));
                        break;
                    case "vt":
                        uvs.Add(new Vector2(Number(words, 1), Number(words, 2)));
                        break;
                    case "vn":
                        normals.Add(new Vector3(0f - Number(words, 1), Number(words, 2), Number(words, 3)));
                        break;
                    case "usemtl":
                        triangles = Material(words.Length > 1 ? words[1] : "", data, materials);
                        break;
                    case "f":
                        if (words.Length < 4)
                            throw new InvalidDataException("OBJ face requires at least three corners.");
                        if (triangles == null)
                            triangles = Material("", data, materials);
                        var face = new int[words.Length - 1];
                        for (int i = 1; i < words.Length; i++)
                        {
                            string[] corner = words[i].Split('/');
                            int p = Index(corner, 0, positions.Count, true);
                            int uv = Index(corner, 1, uvs.Count, false);
                            int n = Index(corner, 2, normals.Count, false);
                            var key = (p, uv, n);
                            if (!corners.TryGetValue(key, out int vertex))
                            {
                                vertex = data.Vertices.Count;
                                corners.Add(key, vertex);
                                data.Vertices.Add(positions[p]);
                                data.Uvs.Add(uv < 0 ? Vector2.zero : uvs[uv]);
                                data.Normals.Add(n < 0 ? Vector3.up : normals[n]);
                                data.HasNormals &= n >= 0;
                            }

                            face[i - 1] = vertex;
                        }

                        for (int i = 1; i < face.Length - 1; i++)
                        {
                            triangles.Add(face[0]);
                            triangles.Add(face[i + 1]);
                            triangles.Add(face[i]);
                        }

                        break;
                }
            }
        }

        if (data.Vertices.Count == 0)
            throw new InvalidDataException("Essentials OBJ contains no faces.");
        return data;
    }

    private static List<int> Material(string name, ObjGeometry data, Dictionary<string, int> materials)
    {
        if (!materials.TryGetValue(name, out int index))
        {
            index = data.Submeshes.Count;
            materials.Add(name, index);
            data.Materials.Add(name);
            data.Submeshes.Add(new List<int>());
        }

        return data.Submeshes[index];
    }

    private static float Number(string[] words, int index)
    {
        if (index >= words.Length || !float.TryParse(words[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number))
            throw new InvalidDataException("Invalid Essentials OBJ coordinate.");
        return number;
    }

    private static int Index(string[] fields, int field, int count, bool required)
    {
        if (field >= fields.Length || fields[field].Length == 0)
        {
            if (required)
                throw new InvalidDataException("OBJ corner has no position.");
            return -1;
        }

        if (!int.TryParse(fields[field], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
            throw new InvalidDataException("Invalid OBJ index.");
        long index = value < 0 ? (long)count + value : (long)value - 1;
        if (index < 0 || index >= count)
            throw new InvalidDataException("OBJ index is outside the model.");
        return (int)index;
    }
}
