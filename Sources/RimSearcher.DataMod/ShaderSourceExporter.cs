using System;
using System.Collections.Generic;
using System.Reflection;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
    public static class ShaderSourceExporter
    {
        public static IEnumerable<string> BuildLines()
        {
            var defType = typeof(Def).Assembly.GetType("Verse.ShaderDef");
            if (defType == null) yield break;
            var programField = defType.GetField("shaderProgram", BindingFlags.Public | BindingFlags.Instance);
            if (programField == null) yield break;

            var resolver = Type.GetType("Waylith.Rendering.ShaderSourceLoader, Waylith")?.GetMethod(
                "TryResolveShaderPath", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string).MakeByRefType() }, null);

            IEnumerable<Def> defs;
            try { defs = GenDefDatabase.GetAllDefsInDatabaseForDef(defType); }
            catch (Exception ex)
            {
                Log.Warning("[RimSearcher] skipping shader sources: " + ex.Message);
                yield break;
            }

            foreach (var def in defs)
            {
                var program = def == null ? null : programField.GetValue(def);
                if (program == null) continue;
                foreach (var stage in new[]
                         {
                             new KeyValuePair<string, string>(IntermediateFormat.ShaderStageVertex, "vertexShaderFileName"),
                             new KeyValuePair<string, string>(IntermediateFormat.ShaderStageFragment, "fragmentShaderFileName"),
                         })
                {
                    var file = program.GetType().GetField(stage.Value, BindingFlags.Public | BindingFlags.Instance)
                                      ?.GetValue(program) as string;
                    if (string.IsNullOrEmpty(file)) continue;

                    var line = new JsonLine()
                        .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindShaderSource)
                        .Str(IntermediateFormat.KeyDefName, def.defName)
                        .Str(IntermediateFormat.KeyShaderStage, stage.Key)
                        .Str(IntermediateFormat.KeyShaderFile, file);
                    if (resolver == null)
                        line.Str(IntermediateFormat.KeyShaderUnresolved, IntermediateFormat.ShaderUnresolvedNoResolver);
                    else
                    {
                        var args = new object[] { file, null };
                        if ((bool)resolver.Invoke(null, args) && args[1] is string path && path.Length > 0)
                            line.Str(IntermediateFormat.KeyShaderResolvedPath, path);
                        else
                            line.Str(IntermediateFormat.KeyShaderUnresolved, IntermediateFormat.ShaderUnresolvedNotFound);
                    }
                    yield return line.ToString();
                }
            }
        }
    }
}
