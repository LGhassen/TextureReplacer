﻿/*
 * Copyright © 2013-2015 Davorin Učakar
 *
 * Permission is hereby granted, free of charge, to any person obtaining a
 * copy of this software and associated documentation files (the "Software"),
 * to deal in the Software without restriction, including without limitation
 * the rights to use, copy, modify, merge, publish, distribute, sublicense,
 * and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
 * THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
 * DEALINGS IN THE SOFTWARE.
 */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TextureReplacer
{
  class Reflections
  {
    public enum Type
    {
      NONE,
      STATIC,
      REAL
    }

    public class Script
    {
      struct MeshState
      {
        public Renderer renderer;
        public bool isEnabled;
      }

      // List of all created reflection scripts.
      static readonly List<Script> scripts = new List<Script>();
      static int currentScript = 0;

      readonly RenderTexture envMap;
      readonly Transform transform;
      readonly bool isEva;
      readonly int interval;
      int counter;
      int currentFace;
      bool isActive = true;

      public Script(Part part, int updateInterval)
      {
        envMap = new RenderTexture(reflectionResolution, reflectionResolution, 24);
        envMap.hideFlags = HideFlags.HideAndDontSave;
        envMap.wrapMode = TextureWrapMode.Clamp;
        envMap.isCubemap = true;

        transform = part.transform;
        isEva = part.GetComponent<KerbalEVA>() != null;

        if (isEva)
        {
          transform = transform.Find("model01");

          SkinnedMeshRenderer visor = transform.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "visor");

          if (visor != null)
          {
            Material material = visor.material;

            material.shader = instance.visorShader;
            material.SetTexture(Util.CUBE_PROPERTY, envMap);
            material.SetColor(Util.REFLECT_COLOR_PROPERTY, visorReflectionColour);
          }
        }

        interval = updateInterval;
        counter = Util.random.Next(updateInterval);
        currentFace = Util.random.Next(6);

        ensureCamera();
        update(true);

        scripts.Add(this);
      }

      public bool apply(Material material, Shader shader, Color reflectionColour)
      {
        Shader reflectiveShader = shader ?? instance.toReflective(material.shader);

        if (reflectiveShader != null)
        {
          material.shader = reflectiveShader;
          material.SetTexture(Util.CUBE_PROPERTY, envMap);
          material.SetColor(Util.REFLECT_COLOR_PROPERTY, reflectionColour);
          return true;
        }
        return false;
      }

      public void destroy()
      {
        scripts.Remove(this);

        Object.DestroyImmediate(envMap);
      }

      void update(bool force)
      {
        int faceMask = force ? 0x3f : 1 << currentFace;

        // Hide all meshes of the current part.
        Renderer[] meshes = transform.GetComponentsInChildren<Renderer>();
        bool[] meshStates = new bool[meshes.Length];

        for (int i = 0; i < meshes.Length; ++i)
        {
          meshStates[i] = meshes[i].enabled;
          meshes[i].enabled = false;
        }

        // Skybox.
        camera.transform.position = GalaxyCubeControl.Instance.transform.position;
        camera.farClipPlane = 100.0f;
        camera.cullingMask = 1 << 18;
        camera.RenderToCubemap(envMap, faceMask);

        // Scaled space.
        camera.transform.position = ScaledSpace.Instance.transform.position;
        camera.farClipPlane = 3.0e7f;
        camera.cullingMask = (1 << 10) | (1 << 23);
        camera.RenderToCubemap(envMap, faceMask);

        // Scene.
        camera.transform.position = isEva ? transform.position + 0.4f * transform.up : transform.position;
        camera.farClipPlane = 60000.0f;
        camera.cullingMask = (1 << 0) | (1 << 1) | (1 << 5) | (1 << 15);
        camera.RenderToCubemap(envMap, faceMask);

        // Restore mesh visibility.
        for (int i = 0; i < meshes.Length; ++i)
          meshes[i].enabled = meshStates[i];

        currentFace = (currentFace + 1) % 6;
      }

      public void setActive(bool value)
      {
        if (!isActive && value)
          update(true);

        isActive = value;
      }

      public static void updateScripts()
      {
        if (scripts.Count != 0 && Time.frameCount % reflectionInterval == 0)
        {
          currentScript %= scripts.Count;

          int startScript = currentScript;
          do
          {
            Script script = scripts[currentScript];
            currentScript = (currentScript + 1) % scripts.Count;

            if (script.isActive)
            {
              script.counter = (script.counter + 1) % script.interval;
              if (script.counter == 0)
              {
                script.update(false);
                break;
              }
            }
          }
          while (currentScript != startScript);
        }
      }
    }

    public static readonly string DIR_ENVMAP = Util.DIR + "EnvMap/";
    // Reflective shader map.
    static readonly string[,] SHADER_MAP = {
      { "KSP/Diffuse", "Reflective/Bumped Diffuse" },
      { "KSP/Specular", "Reflective/Bumped Diffuse" },
      { "KSP/Bumped", "Reflective/Bumped Diffuse" },
      { "KSP/Bumped Specular", "Reflective/Bumped Diffuse" },
      { "KSP/Alpha/Translucent", "TR/Visor" },
      { "KSP/Alpha/Translucent Specular", "TR/Visor" }
    };

    // Render layers:
    //  0 - parts
    //  1 - RCS jets
    //  5 - engine exhaust
    //  9 - sky/atmosphere
    // 10 - scaled space bodies
    // 15 - buildings, terrain
    // 18 - skybox
    // 23 - sun
    static readonly float[] CULL_DISTANCES = {
      1000.0f, 100.0f, 0.0f, 0.0f, 0.0f, 100.0f, 0.0f, 0.0f,
      0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
      0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
      0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
    };
    static readonly Shader transparentSpecularShader = Shader.Find("Transparent/Specular");
    readonly Dictionary<Shader, Shader> shaderMap = new Dictionary<Shader, Shader>();
    // Reflective shader material.
    Material shaderMaterial = null;
    // Reflection camera.
    static Camera camera = null;
    // Environment map textures.
    Cubemap staticEnvMap = null;
    // Reflection type.
    public Type reflectionType = Type.REAL;
    // Real reflection resolution.
    static int reflectionResolution = 128;
    // Interval in frames for updating environment map faces.
    static int reflectionInterval = 2;
    // Reflection colour.
    static Color visorReflectionColour = new Color(0.5f, 0.5f, 0.5f);
    // Visor reflection feature.
    public bool isVisorReflectionEnabled = true;
    // Print names of meshes and their shaders in parts with TRReflection module.
    public bool logReflectiveMeshes = false;
    // Reflective shader.
    Shader visorShader = null;
    // Instance.
    public static Reflections instance = null;

    static void ensureCamera()
    {
      if (camera == null)
      {
        camera = new GameObject("TRReflectionCamera", new[] { typeof(Camera) }).camera;
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.Depth;
        // Any smaller number and visors will refect internals of helmets.
        camera.nearClipPlane = 0.125f;
        camera.layerCullDistances = CULL_DISTANCES;
      }
    }

    /**
     * Get reflective version of a shader.
     */
    public Shader toReflective(Shader shader)
    {
      Shader newShader;
      shaderMap.TryGetValue(shader, out newShader);
      return newShader;
    }

    public bool applyStatic(Material material, Shader shader, Color reflectionColour)
    {
      Shader reflectiveShader = shader ?? toReflective(material.shader);

      if (reflectiveShader != null)
      {
        material.shader = reflectiveShader;
        material.SetTexture(Util.CUBE_PROPERTY, staticEnvMap);
        material.SetColor(Util.REFLECT_COLOR_PROPERTY, reflectionColour);
        return true;
      }
      return false;
    }

    public void setReflectionType(Type type)
    {
      if (type == Type.STATIC && staticEnvMap == null)
        type = Type.NONE;

      reflectionType = type;

      Part[] evas = {
        PartLoader.getPartInfoByName("kerbalEVA").partPrefab,
        PartLoader.getPartInfoByName("kerbalEVAfemale").partPrefab
      };

      for (int i = 0; i < 2; ++i)
      {
        // Set visor texture and reflection on proto-EVA Kerbal.
        SkinnedMeshRenderer visor = evas[i].GetComponentsInChildren<SkinnedMeshRenderer>(true)
          .First(m => m.name == "visor");

        Material material = visor.sharedMaterial;
        bool enableStatic = isVisorReflectionEnabled && reflectionType == Type.STATIC;

        // We apply visor shader for real reflections later, through TREvaModule since we don't
        // want corrupted reflections in the main menu.
        material.shader = enableStatic ? visorShader : transparentSpecularShader;
        material.SetTexture(Util.CUBE_PROPERTY, enableStatic ? staticEnvMap : null);
        material.SetColor(Util.REFLECT_COLOR_PROPERTY, visorReflectionColour);
      }
    }

    /**
     * Read configuration and perform pre-load initialisation.
     */
    public void readConfig(ConfigNode rootNode)
    {
      Util.parse(rootNode.GetValue("reflectionType"), ref reflectionType);
      Util.parse(rootNode.GetValue("reflectionResolution"), ref reflectionResolution);
      Util.parse(rootNode.GetValue("reflectionInterval"), ref reflectionInterval);
      Util.parse(rootNode.GetValue("isVisorReflectionEnabled"), ref isVisorReflectionEnabled);
      Util.parse(rootNode.GetValue("visorReflectionColour"), ref visorReflectionColour);
      Util.parse(rootNode.GetValue("logReflectiveMeshes"), ref logReflectiveMeshes);
    }

    /**
     * Post-load initialisation.
     */
    public void load()
    {
      Texture2D[] envMapFaces = new Texture2D[6];

      foreach (GameDatabase.TextureInfo texInfo in GameDatabase.Instance.databaseTexture)
      {
        Texture2D texture = texInfo.texture;
        if (texture == null || !texture.name.StartsWith(DIR_ENVMAP, System.StringComparison.Ordinal))
          continue;

        string originalName = texture.name.Substring(DIR_ENVMAP.Length);

        switch (originalName)
        {
          case "PositiveX":
            envMapFaces[0] = texture;
            break;
          case "NegativeX":
            envMapFaces[1] = texture;
            break;
          case "PositiveY":
            envMapFaces[2] = texture;
            break;
          case "NegativeY":
            envMapFaces[3] = texture;
            break;
          case "PositiveZ":
            envMapFaces[4] = texture;
            break;
          case "NegativeZ":
            envMapFaces[5] = texture;
            break;
          default:
            Util.log("Invalid enironment map texture name {0}", texture.name);
            break;
        }
      }

      // Generate generic reflection cube map texture.
      if (envMapFaces.Contains(null))
      {
        Util.log("Some environment map faces are missing. Static reflections disabled.");
      }
      else
      {
        int envMapSize = envMapFaces[0].width;

        if (envMapFaces.Any(t => t.width != envMapSize || t.height != envMapSize)
            || envMapFaces.Any(t => !Util.isPow2(t.width) || !Util.isPow2(t.height)))
        {
          Util.log("Invalid environment map faces. Static reflections disabled.");
        }
        else
        {
          try
          {
            staticEnvMap = new Cubemap(envMapSize, TextureFormat.RGB24, true);
            staticEnvMap.hideFlags = HideFlags.HideAndDontSave;
            staticEnvMap.wrapMode = TextureWrapMode.Clamp;
            staticEnvMap.SetPixels(envMapFaces[0].GetPixels(), CubemapFace.PositiveX);
            staticEnvMap.SetPixels(envMapFaces[1].GetPixels(), CubemapFace.NegativeX);
            staticEnvMap.SetPixels(envMapFaces[2].GetPixels(), CubemapFace.PositiveY);
            staticEnvMap.SetPixels(envMapFaces[3].GetPixels(), CubemapFace.NegativeY);
            staticEnvMap.SetPixels(envMapFaces[4].GetPixels(), CubemapFace.PositiveZ);
            staticEnvMap.SetPixels(envMapFaces[5].GetPixels(), CubemapFace.NegativeZ);
            staticEnvMap.Apply(true, false);

            Util.log("Static environment map cube texture generated.");
          }
          catch (UnityException)
          {
            if (staticEnvMap != null)
              Object.DestroyImmediate(staticEnvMap);

            staticEnvMap = null;

            Util.log("Failed to set up static reflections. Textures not readable?");
          }
        }
      }

      try
      {
        Assembly assembly = Assembly.GetExecutingAssembly();
        Stream stream = assembly.GetManifestResourceStream("TextureReplacer.Visor-compiled.shader");
        StreamReader reader = new StreamReader(stream);

        shaderMaterial = new Material(reader.ReadToEnd());
        visorShader = shaderMaterial.shader;

        Util.log("Visor shader sucessfully compiled.");
      }
      catch
      {
        isVisorReflectionEnabled = false;
        Util.log("Visor shader loading failed. Visor reflections disabled.");
      }

      for (int i = 0; i < SHADER_MAP.GetLength(0); ++i)
      {
        Shader original = Shader.Find(SHADER_MAP[i, 0]);
        Shader reflective = Shader.Find(SHADER_MAP[i, 1]);

        if (original == null)
          Util.log("Shader \"{0}\" missing", SHADER_MAP[i, 0]);
        else if (reflective == null)
          Util.log("Shader \"{0}\" missing", SHADER_MAP[i, 1]);
        else
          shaderMap[original] = reflective;
      }

      setReflectionType(reflectionType);
    }

    public void destroy()
    {
      if (staticEnvMap != null)
        Object.DestroyImmediate(staticEnvMap);

      if (camera != null)
        Object.DestroyImmediate(camera.gameObject);

      if (shaderMaterial != null)
        Object.DestroyImmediate(shaderMaterial);
    }

    public void loadScenario(ConfigNode node)
    {
      Type type = reflectionType;
      Util.parse(node.GetValue("reflectionType"), ref type);

      if (type != reflectionType)
        setReflectionType(type);
    }

    public void saveScenario(ConfigNode node)
    {
      node.AddValue("reflectionType", reflectionType);
    }
  }
}
