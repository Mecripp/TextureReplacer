﻿/*
 * Copyright © 2013-2018 Davorin Učakar
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

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Gender = ProtoCrewMember.Gender;

namespace TextureReplacer
{
  class Personaliser
  {
    const string DefaultDirectory = Util.Directory + "Default/";
    const string SkinsDirectory = Util.Directory + "Skins/";
    const string SuitsDirectory = Util.Directory + "Suits/";

    static readonly Log log = new Log(nameof(Personaliser));
    static readonly string[] VeteranNames = { "Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman" };

    // Male/female textures (minus excluded).
    readonly List<Skin>[] kerbalSkins = { new List<Skin>(), new List<Skin>() };
    readonly List<Suit>[] kerbalSuits = { new List<Suit>(), new List<Suit>() };
    // Personalised Kerbal textures.
    readonly Dictionary<string, Appearance> gameKerbals = new Dictionary<string, Appearance>();
    // Backed-up personalised textures from main configuration files. These are used to initialise kerbals if a saved
    // game doesn't contain `TRScenario`.
    ConfigNode customKerbalsNode = new ConfigNode();
    // Cabin-specific suits.
    readonly Dictionary<string, Suit> cabinSuits = new Dictionary<string, Suit>();
    // Helmet removal.
    Mesh[] helmetMesh = { null, null };
    Mesh[] visorMesh = { null, null };
    bool isHelmetRemovalEnabled = true;
    // Convert all females to males but still use female textures for them.
    bool forceLegacyFemales;
    // Atmospheric IVA suit parameters.
    bool isAtmSuitEnabled = true;
    double atmSuitPressure = 50.0;
    readonly HashSet<string> atmSuitBodies = new HashSet<string>();

    // Instance.
    public static Personaliser Instance { get; private set; }

    // Default textures (from `Default/`).
    public Skin[] DefaultSkin = { new Skin { Name = "DEFAULT" }, new Skin { Name = "DEFAULT" } };
    public Suit DefaultSuit = new Suit { Name = "DEFAULT" };

    // All Kerbal textures, including excluded by configuration.
    public List<Skin> Skins = new List<Skin>();
    public List<Suit> Suits = new List<Suit>();

    // Class-specific suits.
    public Dictionary<string, Suit> ClassSuits = new Dictionary<string, Suit>();
    public Dictionary<string, Suit> DefaultClassSuits = new Dictionary<string, Suit>();

    public bool IsHelmetRemovalEnabled {
      get { return isHelmetRemovalEnabled; }
      set { isHelmetRemovalEnabled = value; }
    }

    public bool IsAtmSuitEnabled {
      get { return isAtmSuitEnabled; }
      set { isAtmSuitEnabled = value; }
    }

    /**
     * Whether a vessel is in a "safe" situation, so Kerbals don't need helmets (i.e. landed/splashed or in orbit).
     */
    static bool IsSituationSafe(Vessel vessel)
    {
      return vessel.situation != Vessel.Situations.FLYING && vessel.situation != Vessel.Situations.SUB_ORBITAL;
    }

    /**
     * Whether the atmosphere is breathable.
     */
    public bool IsAtmBreathable()
    {
      bool value = !HighLogic.LoadedSceneIsFlight ||
                   (FlightGlobals.getStaticPressure() >= atmSuitPressure &&
                   atmSuitBodies.Contains(FlightGlobals.currentMainBody.bodyName));
      return value;
    }

    Suit GetClassSuit(ProtoCrewMember kerbal)
    {
      ClassSuits.TryGetValue(kerbal.experienceTrait.Config.Name, out Suit suit);
      return suit;
    }

    public Appearance GetKerbalData(ProtoCrewMember kerbal)
    {
      if (!gameKerbals.TryGetValue(kerbal.name, out Appearance kerbalData)) {
        kerbalData = new Appearance {
          Hash = kerbal.name.GetHashCode(),
          RealGender = kerbal.gender,
          IsVeteran = VeteranNames.Any(n => n == kerbal.name)
        };
        gameKerbals.Add(kerbal.name, kerbalData);

        if (forceLegacyFemales) {
          kerbal.gender = Gender.Male;
        }
      }
      return kerbalData;
    }

    public Skin GetKerbalSkin(ProtoCrewMember kerbal, Appearance kerbalData)
    {
      if (kerbalData.Skin != null) {
        return kerbalData.Skin;
      }

      List<Skin> genderSkins = kerbalSkins[(int)kerbalData.RealGender];
      if (genderSkins.Count == 0) {
        return DefaultSkin[(int)kerbal.gender];
      }

      // Hash is multiplied with a large prime to increase randomisation, since hashes returned by `GetHashCode()` are
      // close together if strings only differ in the last (few) char(s).
      int number = (kerbalData.Hash * 4099) & 0x7fffffff;
      return genderSkins[number % genderSkins.Count];
    }

    public Suit GetKerbalSuit(ProtoCrewMember kerbal, Appearance kerbalData)
    {
      Suit suit = kerbalData.Suit ?? GetClassSuit(kerbal);
      if (suit != null) {
        return suit;
      }

      List<Suit> genderSuits = kerbalSuits[(int)kerbalData.RealGender];
      if (genderSuits.Count == 0) {
        return DefaultSuit;
      }

      // We must use a different prime here to increase randomisation so that the same skin is not always combined with
      // the same suit.
      int number = (kerbalData.Hash * 2053) & 0x7fffffff;
      return genderSuits[number % genderSuits.Count];
    }

    /**
     * Replace textures on a Kerbal model.
     */
    void PersonaliseKerbal(Component component, ProtoCrewMember kerbal, Part cabin, bool needsSuit)
    {
      Appearance kerbalData = GetKerbalData(kerbal);
      bool isEva = cabin == null;

      Skin skin = GetKerbalSkin(kerbal, kerbalData);
      Suit suit = null;

      if (isEva || !cabinSuits.TryGetValue(cabin.partInfo.name, out kerbalData.CabinSuit)) {
        suit = GetKerbalSuit(kerbal, kerbalData);
      }

      skin = skin == DefaultSkin[(int)kerbal.gender] ? null : skin;
      suit = (isEva && needsSuit) || kerbalData.CabinSuit == null ? suit : kerbalData.CabinSuit;
      suit = suit == DefaultSuit ? null : suit;

      Transform model = isEva ? component.transform.Find("model01") : component.transform.Find("kbIVA@idle/model01");
      Transform flag = isEva ? component.transform.Find("model/kbEVA_flagDecals") : null;
      Transform parachute = isEva ? component.transform.Find("model/EVAparachute/base") : null;

      if (isEva) {
        flag.GetComponent<Renderer>().enabled = needsSuit;
        parachute.GetComponent<Renderer>().enabled = needsSuit;
      }

      // We must include hidden meshes, since flares are hidden when light is turned off.
      // All other meshes are always visible, so no performance hit here.
      foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true)) {
        var smr = renderer as SkinnedMeshRenderer;

        // Parachute backpack, flag decals, headlight flares and thruster jets.
        if (smr == null) {
          if (renderer.name != "screenMessage") {
            renderer.enabled = needsSuit;
          }
        } else {
          Material material = renderer.material;
          Texture2D newTexture = null;
          Texture2D newNormalMap = null;

          switch (smr.name) {
            case "eyeballLeft":
            case "eyeballRight":
            case "pupilLeft":
            case "pupilRight":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_eyeballLeft":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_eyeballRight":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_pupilLeft":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_pupilRight":
              if (skin != null && skin.IsEyeless) {
                smr.sharedMesh = null;
              }
              break;

            case "headMesh01":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_pCube1":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_polySurface51":
            case "headMesh":
            case "ponytail":
              if (skin != null) {
                newTexture = skin.Head;
                newNormalMap = skin.HeadNRM;
              }
              break;

            case "tongue":
            case "upTeeth01":
            case "upTeeth02":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_upTeeth01":
            case "mesh_female_kerbalAstronaut01_kerbalGirl_mesh_downTeeth01":
            case "downTeeth01":
              break;

            case "body01":
            case "mesh_female_kerbalAstronaut01_body01":
              bool isEvaSuit = isEva && needsSuit;

              if (suit != null) {
                newTexture = isEvaSuit ? suit.GetEvaSuit(kerbal.experienceLevel) : suit.GetBody(kerbal.experienceLevel);
                newNormalMap = isEvaSuit ? suit.EvaBodyNRM : suit.BodyNRM;
              }

              if (newTexture == null) {
                // This required for two reasons: to fix IVA suits after KSP resetting them to the stock ones all the
                // time and to fix the switch from non-default to default texture during EVA suit toggle.
                newTexture = isEvaSuit ? DefaultSuit.EvaBody
                  : kerbalData.IsVeteran ? DefaultSuit.BodyVeteran
                  : DefaultSuit.Body;
              }

              if (newNormalMap == null) {
                newNormalMap = isEvaSuit ? DefaultSuit.EvaBodyNRM : DefaultSuit.BodyNRM;
              }
              // Update textures in Kerbal IVA object since KSP resets them to these values a few frames later.
              if (!isEva) {
                var kerbalIVA = (Kerbal)component;

                kerbalIVA.textureStandard = newTexture;
                kerbalIVA.textureVeteran = newTexture;
              }
              break;

            case "helmet":
            case "mesh_female_kerbalAstronaut01_helmet":
              if (isEva) {
                smr.enabled = needsSuit;
              } else {
                smr.sharedMesh = needsSuit ? helmetMesh[(int)kerbal.gender] : null;
              }

              // Textures have to be replaced even when hidden since it may become visible later on situation change.
              if (suit != null) {
                newTexture = isEva ? suit.GetEvaHelmet(kerbal.experienceLevel) : suit.GetHelmet(kerbal.experienceLevel);
                newNormalMap = suit.HelmetNRM;
              }
              break;

            case "visor":
            case "mesh_female_kerbalAstronaut01_visor":
              if (isEva) {
                smr.enabled = needsSuit;
              } else {
                smr.sharedMesh = needsSuit ? visorMesh[(int)kerbal.gender] : null;
              }

              // Textures have to be replaced even when hidden since it may become visible later on situation change.
              if (suit != null) {
                newTexture = isEva ? suit.EvaVisor : suit.Visor;

                if (newTexture != null) {
                  material.color = Color.white;
                }
              }
              break;

            default: // Jetpack.
              if (isEva) {
                smr.enabled = needsSuit;

                if (needsSuit && suit != null) {
                  newTexture = suit.EvaJetpack;
                  newNormalMap = suit.EvaJetpackNRM;
                }
              }
              break;
          }

          if (newTexture != null) {
            material.mainTexture = newTexture;
          }
          if (newNormalMap != null) {
            material.SetTexture(Util.BumpMapProperty, newNormalMap);
          }
        }
      }
    }

    /**
     * Personalise Kerbals in an internal space of a vessel. Used by IvaModule.
     */
    public void PersonaliseIva(Kerbal kerbal)
    {
      bool needsSuit = !isHelmetRemovalEnabled || !IsSituationSafe(kerbal.InVessel);

      PersonaliseKerbal(kerbal, kerbal.protoCrewMember, kerbal.InPart, needsSuit);
    }

    /**
     * Set external EVA/IVA suit. Fails and returns false iff trying to remove an EVA suit outside of breathable
     * atmosphere. This function is used by EvaModule.
     */
    public bool PersonaliseEva(Part evaPart, bool useEvaSuit)
    {
      bool isDesiredSuitValid = true;

      if (evaPart.protoModuleCrew.Count != 0) {
        if (!useEvaSuit && !IsAtmBreathable()) {
          useEvaSuit = true;
          isDesiredSuitValid = false;
        }
        PersonaliseKerbal(evaPart, evaPart.protoModuleCrew[0], null, useEvaSuit);
      }
      return isDesiredSuitValid;
    }

    void UpdateHelmets(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> action)
    {
      Vessel vessel = action.host;

      if (!isHelmetRemovalEnabled || vessel == null) {
        return;
      }

      foreach (Part part in vessel.parts.Where(p => p.internalModel != null)) {
        Kerbal[] kerbals = part.internalModel.GetComponentsInChildren<Kerbal>();

        if (kerbals.Length != 0) {
          bool hideHelmets = IsSituationSafe(vessel);

          foreach (Kerbal kerbal in kerbals.Where(k => k.showHelmet)) {
            // `Kerbal.ShowHelmet(false)` irreversibly removes a helmet while
            // `Kerbal.ShowHelmet(true)` has no effect at all. We need the following workaround.
            foreach (SkinnedMeshRenderer smr in kerbal.helmetTransform.GetComponentsInChildren<SkinnedMeshRenderer>()) {
              if (smr.name.EndsWith("helmet", StringComparison.Ordinal)) {
                smr.sharedMesh = hideHelmets ? null : helmetMesh[(int)kerbal.protoCrewMember.gender];
              } else if (smr.name.EndsWith("visor", StringComparison.Ordinal)) {
                smr.sharedMesh = hideHelmets ? null : visorMesh[(int)kerbal.protoCrewMember.gender];
              }
            }
          }
        }
      }
    }

    /**
     * Load per-game custom kerbals mapping.
     */
    void LoadKerbalsMap(ConfigNode node)
    {
      node = node ?? customKerbalsNode;

      KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;

      foreach (ProtoCrewMember kerbal in roster.Crew.Concat(roster.Tourist).Concat(roster.Unowned)) {
        if (kerbal.rosterStatus == ProtoCrewMember.RosterStatus.Dead &&
            kerbal.type != ProtoCrewMember.KerbalType.Unowned) {
          continue;
        }

        Appearance kerbalData = GetKerbalData(kerbal);

        string value = node.GetValue(kerbal.name);
        if (value != null) {
          string[] tokens = Util.SplitConfigValue(value);
          string genderName = tokens.Length >= 1 ? tokens[0] : null;
          string skinName = tokens.Length >= 2 ? tokens[1] : null;
          string suitName = tokens.Length >= 3 ? tokens[2] : null;

          if (genderName != null) {
            kerbalData.RealGender = genderName == "F" ? Gender.Female : Gender.Male;
          }

          if (skinName != null && skinName != "GENERIC") {
            kerbalData.Skin = skinName == "DEFAULT"
              ? DefaultSkin[(int)kerbal.gender]
              : Skins.Find(h => h.Name == skinName);
          }

          if (suitName != null && suitName != "GENERIC") {
            kerbalData.Suit = suitName == "DEFAULT"
              ? DefaultSuit
              : Suits.Find(s => s.Name == suitName);
          }

          kerbal.gender = forceLegacyFemales ? Gender.Male : kerbalData.RealGender;
        }
      }
    }

    /**
     * Save per-game custom Kerbals mapping.
     */
    void SaveKerbals(ConfigNode node)
    {
      KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;

      foreach (ProtoCrewMember kerbal in roster.Crew.Concat(roster.Tourist).Concat(roster.Unowned)) {
        if (kerbal.rosterStatus == ProtoCrewMember.RosterStatus.Dead &&
            kerbal.type != ProtoCrewMember.KerbalType.Unowned) {
          continue;
        }

        Appearance kerbalData = GetKerbalData(kerbal);

        string genderName = kerbalData.RealGender == 0 ? "M" : "F";
        string skinName = kerbalData.Skin == null ? "GENERIC" : kerbalData.Skin.Name;
        string suitName = kerbalData.Suit == null ? "GENERIC" : kerbalData.Suit.Name;

        node.AddValue(kerbal.name, genderName + " " + skinName + " " + suitName);
      }
    }

    /**
     * Load suit mapping.
     */
    void LoadSuitMap(ConfigNode node, IDictionary<string, Suit> map, IDictionary<string, Suit> defaultMap)
    {
      if (node == null) {
        if (defaultMap != null) {
          foreach (var entry in defaultMap) {
            map[entry.Key] = entry.Value;
          }
        }
      } else {
        foreach (ConfigNode.Value entry in node.values) {
          map.Remove(entry.name);

          string suitName = entry.value;
          if (suitName != null && suitName != "GENERIC") {
            if (suitName == "DEFAULT") {
              map[entry.name] = DefaultSuit;
            } else {
              Suit suit = Suits.Find(s => s.Name == suitName);
              if (suit != null) {
                map[entry.name] = suit;
              }
            }
          }
        }
      }
    }

    /**
     * Save suit mapping.
     */
    static void SaveSuitMap(Dictionary<string, Suit> map, ConfigNode node)
    {
      foreach (var entry in map) {
        string suitName = entry.Value == null ? "GENERIC" : entry.Value.Name;

        node.AddValue(entry.Key, suitName);
      }
    }

    /**
     * Fill config for custom Kerbal skinss and suits.
     */
    void ReadKerbalsConfigs()
    {
      var excludedSkins = new List<string>();
      var excludedSuits = new List<string>();
      var femaleSuits = new List<string>();
      var eyelessSkins = new List<string>();

      foreach (UrlDir.UrlConfig file in GameDatabase.Instance.GetConfigs("TextureReplacer")) {
        ConfigNode customNode = file.config.GetNode("CustomKerbals");
        if (customNode != null) {
          // Merge into `customKerbalsNode`.
          foreach (ConfigNode.Value entry in customNode.values) {
            customKerbalsNode.RemoveValue(entry.name);
            customKerbalsNode.AddValue(entry.name, entry.value);
          }
        }

        ConfigNode genericNode = file.config.GetNode("GenericKerbals");
        if (genericNode != null) {
          Util.AddLists(genericNode.GetValues("excludedSkins"), excludedSkins);
          Util.AddLists(genericNode.GetValues("excludedSuits"), excludedSuits);
          Util.AddLists(genericNode.GetValues("femaleSuits"), femaleSuits);
          Util.AddLists(genericNode.GetValues("eyelessSkins"), eyelessSkins);
        }

        ConfigNode classNode = file.config.GetNode("ClassSuits");
        if (classNode != null) {
          LoadSuitMap(classNode, DefaultClassSuits, null);
        }

        ConfigNode cabinNode = file.config.GetNode("CabinSuits");
        if (cabinNode != null) {
          LoadSuitMap(cabinNode, cabinSuits, null);
        }
      }

      // Tag eye-less skins.
      foreach (Skin head in Skins) {
        head.IsEyeless = eyelessSkins.Contains(head.Name);
      }
      // Tag female suits.
      foreach (Suit suit in Suits) {
        suit.Gender = femaleSuits.Contains(suit.Name) ? Gender.Female : Gender.Male;
      }

      // Create lists of male skins and suits.
      kerbalSkins[0].AddRange(Skins.Where(h => h.Gender == Gender.Male && !excludedSkins.Contains(h.Name)));
      kerbalSuits[0].AddRange(Suits.Where(s => s.Gender == Gender.Male && !excludedSuits.Contains(s.Name)));

      // Create lists of female skins and suits. Use same suits as for males unless special female suits are set.
      kerbalSkins[1].AddRange(Skins.Where(h => h.Gender == Gender.Female && !excludedSkins.Contains(h.Name)));
      kerbalSuits[1].AddRange(Suits.Where(s => s.Gender == Gender.Female && !excludedSuits.Contains(s.Name)));
      kerbalSuits[1] = kerbalSuits[1].Count == 0 ? kerbalSuits[0] : kerbalSuits[1];

      // Trim lists.
      Skins.TrimExcess();
      Suits.TrimExcess();
      kerbalSkins[0].TrimExcess();
      kerbalSuits[0].TrimExcess();
      kerbalSkins[1].TrimExcess();
      kerbalSuits[1].TrimExcess();
    }

    public static void Recreate()
    {
      Instance = new Personaliser();
    }

    /**
     * Read configuration and perform pre-load initialisation.
     */
    public void ReadConfig(ConfigNode rootNode)
    {
      Util.Parse(rootNode.GetValue("isHelmetRemovalEnabled"), ref isHelmetRemovalEnabled);
      Util.Parse(rootNode.GetValue("isAtmSuitEnabled"), ref isAtmSuitEnabled);
      Util.Parse(rootNode.GetValue("atmSuitPressure"), ref atmSuitPressure);
      Util.AddLists(rootNode.GetValues("atmSuitBodies"), atmSuitBodies);
      Util.Parse(rootNode.GetValue("forceLegacyFemales"), ref forceLegacyFemales);
    }

    /**
     * Post-load initialisation.
     */
    public void Load()
    {
      var skinDirs = new Dictionary<string, int>();
      var suitDirs = new Dictionary<string, int>();

      foreach (GameDatabase.TextureInfo texInfo in GameDatabase.Instance.databaseTexture) {
        Texture2D texture = texInfo.texture;
        if (texture == null || !texture.name.StartsWith(Util.Directory, StringComparison.Ordinal)) {
          continue;
        }

        // Add a skin texture.
        if (texture.name.StartsWith(SkinsDirectory, StringComparison.Ordinal)) {
          texture.wrapMode = TextureWrapMode.Clamp;

          int lastSlash = texture.name.LastIndexOf('/');
          int dirNameLength = lastSlash - SkinsDirectory.Length;
          string originalName = texture.name.Substring(lastSlash + 1);

          if (dirNameLength < 1) {
            log.Print("Skin texture should be inside a subdirectory: {0}", texture.name);
          } else {
            string dirName = texture.name.Substring(SkinsDirectory.Length, dirNameLength);

            if (!skinDirs.TryGetValue(dirName, out int index)) {
              index = Skins.Count;
              Skins.Add(new Skin { Name = dirName });
              skinDirs.Add(dirName, index);
            }

            Skin skin = Skins[index];
            if (!skin.SetTexture(originalName, texture)) {
              log.Print("Unknown skin texture name \"{0}\": {1}", originalName, texture.name);
            }
          }
        }
        // Add a suit texture.
        else if (texture.name.StartsWith(SuitsDirectory, StringComparison.Ordinal)) {
          texture.wrapMode = TextureWrapMode.Clamp;

          int lastSlash = texture.name.LastIndexOf('/');
          int dirNameLength = lastSlash - SuitsDirectory.Length;
          string originalName = texture.name.Substring(lastSlash + 1);

          if (dirNameLength < 1) {
            log.Print("Suit texture should be inside a subdirectory: {0}", texture.name);
          } else {
            string dirName = texture.name.Substring(SuitsDirectory.Length, dirNameLength);

            if (!suitDirs.TryGetValue(dirName, out int index)) {
              index = Suits.Count;
              Suits.Add(new Suit { Name = dirName });
              suitDirs.Add(dirName, index);
            }

            Suit suit = Suits[index];
            if (!suit.SetTexture(originalName, texture)) {
              log.Print("Unknown suit texture name \"{0}\": {1}", originalName, texture.name);
            }
          }
        } else if (texture.name.StartsWith(DefaultDirectory, StringComparison.Ordinal)) {
          int lastSlash = texture.name.LastIndexOf('/');
          string originalName = texture.name.Substring(lastSlash + 1);

          if (originalName == "kerbalHead") {
            DefaultSkin[0].SetTexture(originalName, texture);
            texture.wrapMode = TextureWrapMode.Clamp;
          } else if (originalName == "kerbalHeadNRM") {
            DefaultSkin[0].SetTexture(originalName, texture);
            texture.wrapMode = TextureWrapMode.Clamp;
          } else if (originalName == "kerbalGirl_06_BaseColor") {
            DefaultSkin[1].SetTexture(originalName, texture);
            texture.wrapMode = TextureWrapMode.Clamp;
          } else if (originalName == "kerbalGirl_06_BaseColorNRM") {
            DefaultSkin[1].SetTexture(originalName, texture);
            texture.wrapMode = TextureWrapMode.Clamp;
          } else if (DefaultSuit.SetTexture(originalName, texture)) {
            texture.wrapMode = TextureWrapMode.Clamp;
          }
        }
      }

      ReadKerbalsConfigs();

      // Initialise default Kerbal, which is only loaded when the main menu shows.
      foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>()) {
        if (texture.name != null) {
          if (texture.name == "kerbalHead") {
            DefaultSkin[0].Head = DefaultSkin[0].Head ?? texture;
          } else if (texture.name == "kerbalGirl_06_BaseColor") {
            DefaultSkin[1].Head = DefaultSkin[1].Head ?? texture;
          } else {
            DefaultSuit.SetTexture(texture.name, texture);
          }
        }
      }

      foreach (Kerbal kerbal in Resources.FindObjectsOfTypeAll<Kerbal>()) {
        int genderIndex = kerbal.transform.name == "kerbalFemale" ? 1 : 0;

        // Save pointer to helmet & visor meshes so helmet removal can restore them.
        foreach (SkinnedMeshRenderer smr in kerbal.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
          if (smr.name.EndsWith("helmet", StringComparison.Ordinal)) {
            helmetMesh[genderIndex] = smr.sharedMesh;
          } else if (smr.name.EndsWith("visor", StringComparison.Ordinal)) {
            visorMesh[genderIndex] = smr.sharedMesh;
          }
        }

        // After na IVA space is initialised, suits are reset to these values. Replace stock textures with default ones.
        kerbal.textureStandard = DefaultSuit.Body;
        kerbal.textureVeteran = DefaultSuit.BodyVeteran;

        if (kerbal.GetComponent<TRIvaModule>() == null) {
          kerbal.gameObject.AddComponent<TRIvaModule>();
        }
      }

      Part[] evas = {
        PartLoader.getPartInfoByName("kerbalEVA").partPrefab,
        PartLoader.getPartInfoByName("kerbalEVAfemale").partPrefab
      };

      foreach (Part eva in evas) {
        if (eva.GetComponent<TREvaModule>() == null) {
          eva.gameObject.AddComponent<TREvaModule>();
        }
      }

      // Re-read scenario if database is reloaded during the space centre scene to avoid losing all per-game settings.
      if (HighLogic.CurrentGame != null) {
        ConfigNode scenarioNode = HighLogic.CurrentGame.config.GetNodes("SCENARIO")
          .FirstOrDefault(n => n.GetValue("name") == "TRScenario");

        if (scenarioNode != null) {
          OnLoadScenario(scenarioNode);
        }
      }
    }

    public void OnBeginFlight()
    {
      GameEvents.onVesselSituationChange.Add(UpdateHelmets);
    }

    public void OnEndFlight()
    {
      GameEvents.onVesselSituationChange.Remove(UpdateHelmets);
    }

    public void OnLoadScenario(ConfigNode node)
    {
      gameKerbals.Clear();
      ClassSuits.Clear();

      LoadKerbalsMap(node.GetNode("Kerbals"));
      LoadSuitMap(node.GetNode("ClassSuits"), ClassSuits, DefaultClassSuits);

      Util.Parse(node.GetValue("isHelmetRemovalEnabled"), ref isHelmetRemovalEnabled);
      Util.Parse(node.GetValue("isAtmSuitEnabled"), ref isAtmSuitEnabled);
    }

    public void OoSaveScenario(ConfigNode node)
    {
      SaveKerbals(node.AddNode("Kerbals"));
      SaveSuitMap(ClassSuits, node.AddNode("ClassSuits"));

      node.AddValue("isHelmetRemovalEnabled", isHelmetRemovalEnabled);
      node.AddValue("isAtmSuitEnabled", isAtmSuitEnabled);
    }

    public void ResetKerbals()
    {
      gameKerbals.Clear();
      ClassSuits.Clear();

      LoadKerbalsMap(null);
      LoadSuitMap(null, ClassSuits, DefaultClassSuits);
    }
  }
}
