using RimeLib.Frostbite;
using RimeLib.Shader;
using RimeLib.Content.Mounting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimeLib.Shader.Frostbite2_0.Frostbite;
using RimeLib.Content.Frostbite;

namespace RimeLib.Shader.Frostbite2_0
{
    public class ShaderResolver : IShaderResolver
    {
        public ShaderDatabaseContainer? ShaderDatabaseContainer { get; private set; }

        public EngineType[] GetSupportedEngines()
        {
            return new[] { EngineType.Frostbite2_0 };
        }

        public ISet<string> GetTextureNames()
        {
            var textureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var shaderDatabase = ShaderDatabaseContainer?.Databases.First().Value;
            if (shaderDatabase == null)
                return textureNames;

            foreach (var constant in shaderDatabase.Constants)
            {
                foreach(var texture in constant.Textures)
                {
                    textureNames.Add(texture.Name);
                }
            }


            return textureNames;
        }

        public ISet<string> GetTextureNamesForShaders(IEnumerable<string> p_ShaderNameFilters)
        {
            var s_TextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var s_ShaderDatabase = ShaderDatabaseContainer?.Databases.First().Value;
            if (s_ShaderDatabase == null)
                return s_TextureNames;

            var s_Filters = p_ShaderNameFilters.Where(p_F => !string.IsNullOrWhiteSpace(p_F))
                                               .Select(p_F => p_F.Trim())
                                               .ToArray();
            if (s_Filters.Length == 0)
                return s_TextureNames;

            foreach (var s_Shader in s_ShaderDatabase.Shaders)
            {
                var s_Matches = s_Filters.Any(p_F =>
                    s_Shader.Key.Contains(p_F, StringComparison.OrdinalIgnoreCase));
                if (!s_Matches)
                    continue;

                // The names the shader itself samples. These are the ones that must be resident on the
                // map the shader is served on -- the engine binds them BY NAME and does not null-check
                // the result, so a missing one is a draw-time crash rather than a missing texture.
                foreach (var s_Texture in s_Shader.Value.StreamableTextures)
                    s_TextureNames.Add(s_Texture.Name);
            }

            return s_TextureNames;
        }

        public void Initialize(IResourceObject resource, IEngineMounter mounter)
        {
            if (resource == null)
                return;

            if (resource.GetResourceType() != ResourceType.DxShaderDatabase && resource.GetResourceType() != ResourceType.IShaderDatabase)
                return;

            ShaderDatabaseContainer = new ShaderDatabaseContainer(resource.GetReader(), mounter);
        }
    }
}
