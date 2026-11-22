using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.IL2CPP;
using BepInEx.Logging;
using TMPro;
using UnhollowerBaseLib;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UI;

namespace ColorblindMode
{
    [BepInPlugin("com.you.colorblindmode", "ColorblindMode", "1.3.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal const string KeyMode = "ColorblindMode.mode";
        internal const string KeyLevel = "ColorblindMode.level";

        public override void Load()
        {
            L = Log;

            ClassInjector.RegisterTypeInIl2Cpp<Runner>();
            var go = new GameObject("ColorblindMode");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();

            L.LogInfo("ColorblindMode loaded");
        }

        internal static string F(float v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- effect
    internal static class Effect
    {
        static PostProcessVolume _vol;
        static ColorGrading _cg;

        // Simulation matrices (Machado et al. 2009, severity 1.0), row-major, linear RGB
        static readonly float[][] Sim =
        {
            new[] { 0.152286f, 1.052583f, -0.204868f,  0.114503f, 0.786281f, 0.099216f,  -0.003882f, -0.048116f, 1.051998f },  // Protanopia
            new[] { 0.367322f, 0.860646f, -0.227968f,  0.280085f, 0.672501f, 0.047413f,  -0.011820f,  0.042940f, 0.968881f },  // Deuteranopia
            new[] { 1.255528f, -0.076749f, -0.178779f, -0.078411f, 0.930809f, 0.147602f,  0.004733f,  0.691367f, 0.303900f },  // Tritanopia
        };

        // Where the color information the person can't see gets moved to
        static readonly float[][] Shift =
        {
            new[] { 0f, 0f, 0f,  0.7f, 1f, 0f,  0.7f, 0f, 1f },
            new[] { 0f, 0f, 0f,  0.7f, 1f, 0f,  0.7f, 0f, 1f },
            new[] { 1f, 0f, 0.7f,  0f, 1f, 0.7f,  0f, 0f, 0f },
        };

        internal static readonly string[] ModeNames = { "Off", "Protanope", "Deuteranope", "Tritanope" };

        static float[] Mul(float[] a, float[] b)
        {
            var r = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float sum = 0f;
                    for (int k = 0; k < 3; k++) sum += a[i * 3 + k] * b[k * 3 + j];
                    r[i * 3 + j] = sum;
                }
            return r;
        }

        // Correction = I + t * Shift * (I - Sim), where t = intensity / 10
        static float[] Build(int mode, int level)
        {
            float[] id = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            float t = Mathf.Clamp01(level / 10f);
            if (mode < 1 || mode > 3 || t <= 0f) return id;

            float[] s = Sim[mode - 1];
            var e = new float[9];
            for (int i = 0; i < 9; i++) e[i] = id[i] - s[i];

            float[] me = Mul(Shift[mode - 1], e);
            var c = new float[9];
            for (int i = 0; i < 9; i++) c[i] = id[i] + t * me[i];
            return c;
        }

        static void EnsureVolume()
        {
            if (_vol != null && _cg != null) return;

            var cam = Camera.main;
            if (cam == null) return;
            var layer = cam.GetComponent<PostProcessLayer>();
            if (layer == null) return;

            // Use a layer that this camera's post-processing actually listens to
            int mask = layer.volumeLayer.value;
            int idx = cam.gameObject.layer;
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0) { idx = i; break; }
            }

            _cg = ScriptableObject.CreateInstance<ColorGrading>();
            _cg.enabled.Override(true);
            _cg.gradingMode.Override(GradingMode.LowDefinitionRange);

            var arr = new Il2CppReferenceArray<PostProcessEffectSettings>(
                new PostProcessEffectSettings[] { _cg });
            _vol = PostProcessManager.instance.QuickVolume(idx, 1000f, arr);
            UnityEngine.Object.DontDestroyOnLoad(_vol.gameObject);

            Plugin.L.LogInfo("Created colorblind volume on layer " + idx);
        }

        internal static void Apply(int mode, int level)
        {
            try
            {
                EnsureVolume();
                if (_vol == null || _cg == null) return;

                float[] c = Build(mode, level);
                _cg.mixerRedOutRedIn.Override(c[0] * 100f);
                _cg.mixerRedOutGreenIn.Override(c[1] * 100f);
                _cg.mixerRedOutBlueIn.Override(c[2] * 100f);
                _cg.mixerGreenOutRedIn.Override(c[3] * 100f);
                _cg.mixerGreenOutGreenIn.Override(c[4] * 100f);
                _cg.mixerGreenOutBlueIn.Override(c[5] * 100f);
                _cg.mixerBlueOutRedIn.Override(c[6] * 100f);
                _cg.mixerBlueOutGreenIn.Override(c[7] * 100f);
                _cg.mixerBlueOutBlueIn.Override(c[8] * 100f);

                _vol.weight = (mode == 0 || level == 0) ? 0f : 1f;
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Effect.Apply failed: " + e);
            }
        }
    }

    // ---------------------------------------------------------------- settings rows + saving
    public class Runner : MonoBehaviour
    {
        public Runner(IntPtr ptr) : base(ptr) { }

        const string TemplateName = "ScrollSettingBloom";
        const string RowModeName = "ColorblindModeRow";
        const string RowLevelName = "ColorblindIntensityRow";

        static readonly string[] ModeNames = Effect.ModeNames;
        static readonly string[] LevelNames = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" };

        static Runner _instance;
        bool _hooked;

        MonoBehaviour1PublicTeseStRascRascUnique _modeRow;
        MonoBehaviour1PublicTeseStRascRascUnique _levelRow;
        int _mode;
        int _level = 5;
        float _nextCheck;
        bool _loaded;

        // Scroll position protection (does not rely on ScrollRect)
        RectTransform _content;
        Scrollbar _bar;
        float _cy1, _cy2;          // content y, 1 and 2 frames ago
        float _by1, _by2;          // scrollbar value, 1 and 2 frames ago
        float _holdCy, _holdBy;
        int _holdUntilFrame = -1;
        int _traceFrames;
        float _tcy, _tbv;
        bool _loggedRestore;

        void Update()
        {
            _instance = this;
            if (!_hooked) HookRender();

            if (!_loaded)
            {
                _loaded = true;
                _mode = Mathf.Clamp(PlayerPrefs.GetInt(Plugin.KeyMode, 0), 0, 3);
                _level = Mathf.Clamp(PlayerPrefs.GetInt(Plugin.KeyLevel, 5), 0, 10);
                Plugin.L.LogInfo("Loaded saved colorblind settings: " + ModeNames[_mode] + ", intensity " + _level);
            }

            float now = Time.unscaledTime;
            if (now >= _nextCheck)
            {
                _nextCheck = now + 1f;

                if (_modeRow == null || _levelRow == null)
                    TryBuildRows();

                // Re-apply once a second so the filter survives scene changes
                if (_mode != 0)
                    Effect.Apply(_mode, _level);
            }

            if (_modeRow != null && _levelRow != null)
                CheckRows();
        }

        // ------------------------------------------------ hook right before the UI is drawn
        void HookRender()
        {
            _hooked = true;
            try
            {
                MethodInfo adder = null;
                Type handlerType = null;

                var ev = typeof(Canvas).GetEvent("willRenderCanvases", BindingFlags.Public | BindingFlags.Static);
                if (ev != null)
                {
                    adder = ev.GetAddMethod();
                    handlerType = ev.EventHandlerType;
                }
                else
                {
                    adder = typeof(Canvas).GetMethod("add_willRenderCanvases", BindingFlags.Public | BindingFlags.Static);
                    if (adder != null) handlerType = adder.GetParameters()[0].ParameterType;
                }

                if (adder == null || handlerType == null)
                {
                    Plugin.L.LogWarning("Canvas.willRenderCanvases not found; the list may still flicker for one frame");
                    return;
                }

                Action callback = OnWillRenderCanvases;
                object del = null;

                var conv = handlerType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                                                 null, new Type[] { typeof(Action) }, null);
                if (conv != null) del = conv.Invoke(null, new object[] { callback });

                if (del == null)
                {
                    var support = typeof(ClassInjector).Assembly.GetType("UnhollowerRuntimeLib.DelegateSupport");
                    var m = support != null ? support.GetMethod("ConvertDelegate") : null;
                    if (m != null) del = m.MakeGenericMethod(handlerType).Invoke(null, new object[] { callback });
                }

                if (del == null)
                {
                    Plugin.L.LogWarning("Could not create a delegate for Canvas.willRenderCanvases; the list may still flicker for one frame");
                    return;
                }

                adder.Invoke(null, new object[] { del });
                Plugin.L.LogInfo("Hooked Canvas.willRenderCanvases");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Could not hook Canvas.willRenderCanvases: " + e.Message);
            }
        }

        static void OnWillRenderCanvases()
        {
            try
            {
                var r = _instance;
                if (r != null) r.Protect("render");
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Render hook failed: " + e);
            }
        }

        // ------------------------------------------------ scroll protection
        bool Holding()
        {
            return Time.frameCount <= _holdUntilFrame;
        }

        void Protect(string where)
        {
            if (_content == null) return;
            if (!Holding()) return;

            float cy = _content.anchoredPosition.y;
            float bv = _bar != null ? _bar.value : 0f;

            bool moved = Mathf.Abs(cy - _holdCy) > 0.5f ||
                         (_bar != null && Mathf.Abs(bv - _holdBy) > 0.002f);
            if (!moved) return;

            if (!_loggedRestore)
            {
                _loggedRestore = true;
                Plugin.L.LogInfo("Scroll reset detected in " + where + " (contentY " + Plugin.F(_holdCy) + " -> " + Plugin.F(cy) +
                                 ", scrollbar " + _holdBy.ToString("0.0000", CultureInfo.InvariantCulture) + " -> " +
                                 bv.ToString("0.0000", CultureInfo.InvariantCulture) + "), restoring");
            }

            if (_bar != null) _bar.value = _holdBy;
            _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, _holdCy);
        }

        void LateUpdate()
        {
            if (_content == null) return;

            float cy = _content.anchoredPosition.y;
            float bv = _bar != null ? _bar.value : 0f;

            // Log what moves after a click, to see what resets the list
            if (_traceFrames > 0)
            {
                _traceFrames--;
                if (Mathf.Abs(cy - _tcy) > 0.01f || Mathf.Abs(bv - _tbv) > 0.0001f)
                {
                    Plugin.L.LogInfo("TRACE frame " + Time.frameCount + ": contentY=" + Plugin.F(cy) +
                                     " scrollbar=" + bv.ToString("0.0000", CultureInfo.InvariantCulture));
                    _tcy = cy;
                    _tbv = bv;
                }
            }

            if (Holding())
            {
                Protect("late");
                return;
            }

            _cy2 = _cy1; _cy1 = cy;
            _by2 = _by1; _by1 = bv;
        }

        // ------------------------------------------------ rows
        void TryBuildRows()
        {
            MonoBehaviour1PublicTeseStRascRascUnique template = null;

            foreach (var o in Resources.FindObjectsOfTypeAll(UnhollowerRuntimeLib.Il2CppType.Of<MonoBehaviour1PublicTeseStRascRascUnique>()))
            {
                var sel = o.TryCast<MonoBehaviour1PublicTeseStRascRascUnique>();
                if (sel == null) continue;
                if (!sel.gameObject.scene.IsValid()) continue;   // skip prefab assets
                if (sel.gameObject.name == TemplateName) { template = sel; break; }
            }

            // Settings menu not built yet: try again next second
            if (template == null) return;

            Transform parent = template.transform.parent;

            // Remove leftovers of an earlier build, if any
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var ch = parent.GetChild(i);
                if (ch.name == RowModeName || ch.name == RowLevelName)
                    UnityEngine.Object.Destroy(ch.gameObject);
            }

            _modeRow = MakeRow(template, RowModeName, "Colorblind Mode", ModeNames, _mode);
            _levelRow = MakeRow(template, RowLevelName, "Colorblind Intensity", LevelNames, _level);

            SetupScrollTracking(_levelRow.transform);

            Plugin.L.LogInfo("Added colorblind rows to the Graphics tab");
        }

        static string PathOf(Transform t)
        {
            string p = t.name;
            for (var q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
            return p;
        }

        static void LogComps(string prefix, Transform t)
        {
            var names = new List<string>();
            foreach (var c in t.GetComponents<UnityEngine.Component>())
            {
                if (c == null) continue;
                names.Add(c.GetIl2CppType().FullName);
            }
            Plugin.L.LogInfo(prefix + PathOf(t) + " [" + string.Join(", ", names.ToArray()) + "]");
        }

        void SetupScrollTracking(Transform row)
        {
            _content = null;
            _bar = null;

            Transform content = row.parent;
            if (content == null) return;

            _content = content.GetComponent<RectTransform>();

            // Look for a scrollbar next to the list (the player list uses Container/ScrollBar)
            Transform search = content.parent;
            for (int up = 0; up < 3 && search != null && _bar == null; up++)
            {
                _bar = search.GetComponentInChildren<Scrollbar>();
                search = search.parent;
            }

            var rt = _content;
            Plugin.L.LogInfo("Scroll tracking: content=" + PathOf(content) + " pos=" + rt.anchoredPosition +
                             " pivot=" + rt.pivot + " size=" + rt.sizeDelta);
            if (content.parent != null) LogComps("  container: ", content.parent);
            LogComps("  content: ", content);

            if (_bar != null)
                Plugin.L.LogInfo("  scrollbar: " + PathOf(_bar.transform) + " value=" + _bar.value.ToString("0.0000", CultureInfo.InvariantCulture) +
                                 " size=" + Plugin.F(_bar.size) + " direction=" + _bar.direction);
            else
                Plugin.L.LogWarning("  no Scrollbar found near the list");

            _cy1 = _content.anchoredPosition.y; _cy2 = _cy1;
            _by1 = _bar != null ? _bar.value : 0f; _by2 = _by1;
        }

        static MonoBehaviour1PublicTeseStRascRascUnique MakeRow(
            MonoBehaviour1PublicTeseStRascRascUnique template, string name, string label, string[] options, int index)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            go.name = name;

            var labelTr = go.transform.Find("Text/Text (TMP)");
            if (labelTr != null)
            {
                var tmp = labelTr.GetComponent<TextMeshProUGUI>();
                if (tmp != null) tmp.text = label;
            }
            else
            {
                Plugin.L.LogWarning("Label object not found in cloned row " + name);
            }

            var sel = go.GetComponent<MonoBehaviour1PublicTeseStRascRascUnique>();

            // Stop the clone from triggering the original Bloom setting (same trick MoreSettings uses)
            sel.m_OnClick = new();

            sel.SetSettings(new Il2CppStringArray(options), index);
            sel.currentSetting = index;

            var st = sel.settingText;
            if (st != null)
            {
                // Keep long names like "Deuteranope" on one line
                st.enableWordWrapping = false;
                st.enableAutoSizing = true;
                st.fontSizeMax = st.fontSize;
                st.fontSizeMin = 12f;
                st.text = options[index];
            }

            go.SetActive(true);
            return sel;
        }

        // Works out which option a row currently shows, from its label text
        static int ReadIndex(MonoBehaviour1PublicTeseStRascRascUnique row, string[] names)
        {
            if (row.settingText != null)
            {
                string t = row.settingText.text;
                if (t != null)
                {
                    int i = Array.IndexOf(names, t.Trim());
                    if (i >= 0) return i;
                }
            }
            return Mathf.Clamp(row.currentSetting, 0, names.Length - 1);
        }

        void CheckRows()
        {
            int m = ReadIndex(_modeRow, ModeNames);
            int l = ReadIndex(_levelRow, LevelNames);
            if (m == _mode && l == _level) return;

            _mode = m;
            _level = l;

            // A click happened: hold the list where it was a couple of frames ago
            if (_content != null)
            {
                _holdCy = _cy2;
                _holdBy = _by2;
                _holdUntilFrame = Time.frameCount + 30;
                _traceFrames = 40;
                _tcy = _cy2;
                _tbv = _by2;
                _loggedRestore = false;
            }

            PlayerPrefs.SetInt(Plugin.KeyMode, _mode);
            PlayerPrefs.SetInt(Plugin.KeyLevel, _level);
            PlayerPrefs.Save();

            Plugin.L.LogInfo("Colorblind: " + ModeNames[_mode] + ", intensity " + _level);
            Effect.Apply(_mode, _level);
        }
    }
}