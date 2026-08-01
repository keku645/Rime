using RimeLib.Content.Mounting;
using System.Collections.Generic;

namespace RimeLib.Shader
{
    public interface IShaderResolver : IEngineInterface
    {
        ISet<string> GetTextureNames();

        /// <summary>
        /// Texture names sampled by SPECIFIC shaders only.
        ///
        /// GetTextureNames() returns everything the database's constant tables reference, which for a
        /// level database is that level's entire texture set -- hundreds of names. When a bundle ships
        /// a handful of foreign shaders, making all of those resident is both pointless and dangerous,
        /// so this narrows the answer to the shaders actually being shipped.
        /// </summary>
        /// <param name="p_ShaderNameFilters">
        /// Substrings matched case-insensitively against shader names. A shader is included when any
        /// filter occurs in its name, so a folder prefix selects a whole family.
        /// </param>
        ISet<string> GetTextureNamesForShaders(IEnumerable<string> p_ShaderNameFilters);

        void Initialize(IResourceObject resource, IEngineMounter mounter);
    }
}
