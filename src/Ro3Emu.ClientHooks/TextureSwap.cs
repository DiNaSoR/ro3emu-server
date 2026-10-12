using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Replacements for UI images, e.g. English versions of images that bake Chinese text (result banners, season and
    /// event titles ...). Replacements are PNGs named after the original texture / sprite, embedded in this DLL from
    /// Textures/ (tools/ClientTextures renders them from a dump of your own client; none are distributed with the
    /// source). A texture without a replacement is drawn as the client ships it.
    /// The patcher makes UnityEngine.UI call in here (ClientTools ImageSwapPatch):
    /// <list type="bullet">
    /// <item>RawImage.texture and Image.sprite setters pass the new value through <see cref="MapTexture"/> /
    /// <see cref="MapSprite"/>: Lua loads, graphic-state switches and language reloads all assign through them;</item>
    /// <item>Graphic.OnEnable calls <see cref="Enabled"/>: images deserialized from a prefab never pass a setter.</item>
    /// </list>
    /// A replaced sprite keeps the original's pivot, border and pixels per unit, so sliced / masked images lay out alike.
    /// Only the resource names are read up front; a PNG is decoded the first time its original is drawn.
    /// </summary>
    public static class TextureSwap
    {
        private const string ResourcePrefix = "textures/";

        /// <summary>texture name -> embedded resource name, then decoded textures as they are first needed.</summary>
        private static Dictionary<string, string> _resources;
        private static readonly Dictionary<string, Texture2D> Decoded = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Sprite> Sprites = new Dictionary<int, Sprite>();
        private static readonly HashSet<int> Ours = new HashSet<int>();

        private static Dictionary<string, string> Resources
        {
            get
            {
                if (_resources == null) Index();
                return _resources;
            }
        }

        /// <summary>RawImage.texture = MapTexture(value).</summary>
        public static Texture MapTexture(Texture texture)
        {
            try
            {
                if (texture == null || Resources.Count == 0 || Ours.Contains(texture.GetInstanceID())) return texture;
                return TryGet(texture.name, out var replacement) ? replacement : texture;
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.TextureSwap] " + e);
                return texture;
            }
        }

        /// <summary>Image.sprite = MapSprite(value): a sprite built from the replacement with the original's geometry.</summary>
        public static Sprite MapSprite(Sprite sprite)
        {
            try
            {
                if (sprite == null || Resources.Count == 0) return sprite;
                int id = sprite.GetInstanceID();
                if (Ours.Contains(id)) return sprite;
                if (Sprites.TryGetValue(id, out var made)) return made;
                if (!TryGet(sprite.name, out var texture)) return sprite;
                var rect = sprite.rect;
                var pivot = new Vector2(rect.width > 0 ? sprite.pivot.x / rect.width : 0.5f, rect.height > 0 ? sprite.pivot.y / rect.height : 0.5f);
                made = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot,
                    sprite.pixelsPerUnit * texture.width / Math.Max(1f, rect.width), 0, SpriteMeshType.FullRect, sprite.border);
                made.name = sprite.name;
                Sprites[id] = made;
                Ours.Add(made.GetInstanceID());
                return made;
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.TextureSwap] " + e);
                return sprite;
            }
        }

        /// <summary>
        /// A sprite of our own (embedded "textures/&lt;name&gt;.png" that replaces no client texture), sized like
        /// <paramref name="like"/> so it drops into the same Image; null when the DLL carries no such texture.
        /// </summary>
        public static Sprite Custom(string name, Sprite like)
        {
            if (!TryGet(name, out var texture)) return null;
            float ppu = like != null ? like.pixelsPerUnit * texture.width / Math.Max(1f, like.rect.width) : 100f;
            var made = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
            made.name = name;
            Ours.Add(made.GetInstanceID());
            return made;
        }

        /// <summary>Graphic.OnEnable: swap what a prefab serialized.</summary>
        public static void Enabled(UnityEngine.Object graphic)
        {
            if (Resources.Count == 0) return;
            if (graphic is RawImage raw)
            {
                var mapped = MapTexture(raw.texture);
                if (mapped != raw.texture) raw.texture = mapped;
            }
            else if (graphic is Image image)
            {
                var mapped = MapSprite(image.sprite);
                if (mapped != image.sprite) image.sprite = mapped;
            }
        }

        private static bool TryGet(string name, out Texture2D texture)
        {
            texture = null;
            if (name == null || !Resources.TryGetValue(name, out var resource)) return false;
            if (Decoded.TryGetValue(name, out texture)) return texture != null;
            byte[] png;
            var asm = typeof(TextureSwap).Assembly;
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                png = copy.ToArray();
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name };
            if (!ImageConversion.LoadImage(tex, png, true)) tex = null;
            else Ours.Add(tex.GetInstanceID());
            Decoded[name] = tex;
            texture = tex;
            return tex != null;
        }

        /// <summary>The replacements travel inside this DLL (EmbeddedResource "textures/&lt;name&gt;.png").</summary>
        private static void Index()
        {
            _resources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in typeof(TextureSwap).Assembly.GetManifestResourceNames())
                if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                    _resources[Path.GetFileNameWithoutExtension(resource.Substring(ResourcePrefix.Length))] = resource;
            Debug.Log("[Ro3Emu.TextureSwap] " + _resources.Count + " embedded replacement(s)");
        }
    }
}
