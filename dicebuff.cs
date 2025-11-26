using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

namespace dicebuff
{
    // 주사위를 인벤에 들고 있으면 주기적으로 주사위를 굴리고,
    // 눈(1~6)에 따라 이속/방어력/공격력을 바꾸는 모드입니다.
    // + MapLevel 모드가 있으면, 주사위 눈에 따라 맵 난이도도 함께 변경합니다.
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                Debug.Log("[MutatorDice] OnAfterSetup - Dice 버프 초기화 시작");

                GameObject root = new GameObject("MutatorDiceRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);

                Debug.Log("[MutatorDice] DiceBuffController 추가 시도");
                root.AddComponent<DiceBuffController>();
                Debug.Log("[MutatorDice] DiceBuffController 추가 완료: OK");

                Debug.Log("[MutatorDice] DiceBuffHUD 추가 시도");
                root.AddComponent<DiceBuffHUD>();
                Debug.Log("[MutatorDice] DiceBuffHUD 추가 완료: OK");

                Debug.Log("[MutatorDice] DiceDamagePatch.ApplyPatches 시작");
                DiceDamagePatch.ApplyPatches();

                // MapLevel 연동 브리지 (있으면만 동작)
                MapLevelBridge.TryInitialize();
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] OnAfterSetup 예외: " + ex);
            }
        }

        protected override void OnBeforeDeactivate()
        {
            Debug.Log("[MutatorDice] OnBeforeDeactivate - 언로드");
            DiceBuffController.ResetBuffs();
        }
    }

    public class DiceBuffController : MonoBehaviour
    {
        // VanillaLootCollection JSON에 정의된 주사위 ID
        internal const int SilverDiceId = 30500;
        internal const int GoldDiceId = 30501;

        private static DiceBuffController _instance;
        public static DiceBuffController Instance
        {
            get { return _instance; }
        }

        private CharacterMainControl _player;
        private object _playerHealth; // 타입 Health를 직접 쓰지 않고 object로만 유지

        private float _nextPlayerSearchTime;
        private const float PlayerSearchInterval = 2f;

        private float _nextInventoryScanTime;
        private const float InventoryScanInterval = 2f;

        private float _nextRollTime;
        private const float RollInterval = 300f; // 5분마다 자동 굴림

        private readonly System.Random _rng = new System.Random();

        // 현재 버프 상태 (정적, 패치에서 사용)
        public static bool HasDice { get; private set; }
        public static int CurrentRoll { get; private set; }
        public static float MoveSpeedMultiplier { get; private set; } = 1f;
        public static float DamageTakenMultiplier { get; private set; } = 1f; // 플레이어가 맞을 때
        public static float DamageDealtMultiplier { get; private set; } = 1f; // 플레이어가 때릴 때

        public static object PlayerHealth { get; private set; }

        // 이속 조절용 캐시
        private readonly List<FieldInfo> _speedFields = new List<FieldInfo>();
        private readonly Dictionary<FieldInfo, float> _speedBaseValues = new Dictionary<FieldInfo, float>();
        private bool _speedFieldsInitialized;

        // Item 리플렉션용 캐시
        private static FieldInfo _itemTypeIdField;
        private static FieldInfo _itemDisplayNameField;
        private static bool _itemFieldsResolved;

        // 이름 기반 주사위 판정 키워드
        private static readonly string[] DiceNameKeywords = new string[]
        {
            "주사위",
            "dice",
            "golddice",
            "silverdice"
        };

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.Log("[MutatorDice] DiceBuffController.Awake - 기존 인스턴스 제거");
                Destroy(this.gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(this.gameObject);

            Debug.Log("[MutatorDice] DiceBuffController.Awake 완료");
        }

        private void OnEnable()
        {
            Debug.Log("[MutatorDice] DiceBuffController.OnEnable");
        }

        public CharacterMainControl GetPlayer()
        {
            return _player;
        }

        private void Update()
        {
            try
            {
                UpdatePlayer();
                UpdateDiceState();
                UpdateAutoRoll();
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] Update 예외: " + ex);
            }
        }

        // ─────────────────────────────────────
        // 플레이어 / Health / 이속 필드 찾기
        // ─────────────────────────────────────
        private void UpdatePlayer()
        {
            if (_player == null)
            {
                if (Time.time < _nextPlayerSearchTime)
                    return;

                _nextPlayerSearchTime = Time.time + PlayerSearchInterval;

                CharacterMainControl main = CharacterMainControl.Main;
                if (main != null)
                {
                    _player = main;
                    Debug.Log("[MutatorDice] 플레이어 찾음: " + _player.name);

                    TryResolvePlayerHealth();
                    TryCacheSpeedFields();
                    ApplyMoveSpeedBuff();
                }
                else
                {
                    Debug.Log("[MutatorDice] CharacterMainControl.Main 이 null");
                }

                return;
            }

            if (_playerHealth == null || PlayerHealth == null)
            {
                TryResolvePlayerHealth();
            }

            if (!_speedFieldsInitialized)
            {
                TryCacheSpeedFields();
                ApplyMoveSpeedBuff();
            }
        }

        private void TryResolvePlayerHealth()
        {
            if (_player == null)
                return;

            if (_playerHealth != null && PlayerHealth != null)
                return;

            try
            {
                Type t = _player.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo pi = t.GetProperty("Health", flags);
                if (pi != null)
                {
                    object v = null;
                    try
                    {
                        v = pi.GetValue(_player, null);
                    }
                    catch
                    {
                    }

                    if (v != null)
                    {
                        _playerHealth = v;
                        PlayerHealth = v;
                        Debug.Log("[MutatorDice] 플레이어 Health 프로퍼티에서 Health 획득");
                        return;
                    }
                }

                Debug.Log("[MutatorDice] 플레이어 Health 프로퍼티를 찾지 못했거나 null");
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] TryResolvePlayerHealth 예외: " + ex);
            }
        }

        private void TryCacheSpeedFields()
        {
            if (_player == null)
                return;

            if (_speedFieldsInitialized)
                return;

            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo[] fields = _player.GetType().GetFields(flags);

                int count = 0;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo fi = fields[i];
                    if (fi.FieldType != typeof(float))
                        continue;

                    string name = fi.Name.ToLowerInvariant();
                    if (!name.Contains("speed") && !name.Contains("move"))
                        continue;

                    try
                    {
                        object v = fi.GetValue(_player);
                        if (v is float f)
                        {
                            _speedFields.Add(fi);
                            _speedBaseValues[fi] = f;
                            count++;
                        }
                    }
                    catch
                    {
                    }
                }

                Debug.Log("[MutatorDice] 이속 관련 필드 캐시 완료: count=" + count);
                _speedFieldsInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] TryCacheSpeedFields 예외: " + ex);
            }
        }

        internal void ApplyMoveSpeedBuff()
        {
            try
            {
                if (_player == null)
                    return;

                if (!_speedFieldsInitialized)
                {
                    TryCacheSpeedFields();
                }

                if (_speedFields.Count == 0)
                    return;

                float mul = MoveSpeedMultiplier;
                for (int i = 0; i < _speedFields.Count; i++)
                {
                    FieldInfo fi = _speedFields[i];
                    float baseVal;
                    if (!_speedBaseValues.TryGetValue(fi, out baseVal))
                        continue;

                    float newVal = baseVal * mul;
                    try
                    {
                        fi.SetValue(_player, newVal);
                    }
                    catch
                    {
                    }
                }

                Debug.Log(string.Format(
                    "[MutatorDice] 이속 버프 적용: x{0:F2} (fields={1})",
                    mul, _speedFields.Count));
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] ApplyMoveSpeedBuff 예외: " + ex);
            }
        }

        // ─────────────────────────────────────
        // 인벤 / 주사위 상태 업데이트
        // ─────────────────────────────────────
        private void UpdateDiceState()
        {
            if (_player == null)
                return;

            if (Time.time < _nextInventoryScanTime)
                return;

            _nextInventoryScanTime = Time.time + InventoryScanInterval;

            bool hasDiceNow = ScanMainInventoryForDice(_player);

            Debug.Log("[MutatorDice] UpdateDiceState - 인벤 스캔 결과: hasDice=" + hasDiceNow);

            if (hasDiceNow != HasDice)
            {
                HasDice = hasDiceNow;

                if (!HasDice)
                {
                    Debug.Log("[MutatorDice] 주사위를 잃어버림 - 버프 해제");
                    ResetBuffs();
                }
                else
                {
                    Debug.Log("[MutatorDice] 주사위를 보유 중 - 자동 버프 활성");
                    _nextRollTime = Time.time; // 즉시 첫 굴림
                }
            }
        }

        private void UpdateAutoRoll()
        {
            if (!HasDice)
                return;

            if (Time.time < _nextRollTime)
                return;

            RollDice();
        }

        private void RollDice()
        {
            int roll = _rng.Next(1, 7); // 1~6
            CurrentRoll = roll;

            float moveMul;
            float dmgTakenMul;
            float dmgDealtMul;
            GetBuffForRoll(roll, out moveMul, out dmgTakenMul, out dmgDealtMul);

            MoveSpeedMultiplier = moveMul;
            DamageTakenMultiplier = dmgTakenMul;
            DamageDealtMultiplier = dmgDealtMul;

            _nextRollTime = Time.time + RollInterval;

            Debug.Log(string.Format(
                "[MutatorDice] 주사위 굴림: {0} -> 이속 x{1:F2}, 받는 피해 x{2:F2}, 주는 피해 x{3:F2}",
                roll, MoveSpeedMultiplier, DamageTakenMultiplier, DamageDealtMultiplier));

            ApplyMoveSpeedBuff();

            DiceBuffHUD hud = DiceBuffHUD.Instance;
            if (hud != null)
            {
                hud.OnDiceRoll(roll, moveMul, dmgTakenMul, dmgDealtMul);
            }

            // MapLevel 연동 (있는 경우에만 동작)
            MapLevelBridge.ApplyDiceRollToMap(roll);
        }

        private static void GetBuffForRoll(
            int roll,
            out float moveMul,
            out float damageTakenMul,
            out float damageDealtMul)
        {
            switch (roll)
            {
                case 1:
                    moveMul = 0.80f;
                    damageTakenMul = 1.20f;
                    damageDealtMul = 0.90f;
                    break;

                case 2:
                    moveMul = 0.90f;
                    damageTakenMul = 1.10f;
                    damageDealtMul = 0.95f;
                    break;

                case 3:
                    moveMul = 1.00f;
                    damageTakenMul = 1.00f;
                    damageDealtMul = 1.00f;
                    break;

                case 4:
                    moveMul = 1.05f;
                    damageTakenMul = 0.95f;
                    damageDealtMul = 1.10f;
                    break;

                case 5:
                    moveMul = 1.15f;
                    damageTakenMul = 0.85f;
                    damageDealtMul = 1.25f;
                    break;

                case 6:
                    moveMul = 1.25f;
                    damageTakenMul = 0.75f;
                    damageDealtMul = 1.40f;
                    break;

                default:
                    moveMul = 1.0f;
                    damageTakenMul = 1.0f;
                    damageDealtMul = 1.0f;
                    break;
            }
        }

        public static void ResetBuffs()
        {
            CurrentRoll = 0;
            MoveSpeedMultiplier = 1f;
            DamageTakenMultiplier = 1f;
            DamageDealtMultiplier = 1f;

            if (Instance != null)
            {
                Instance.ApplyMoveSpeedBuff();
            }
        }

        // ─────────────────────────────────────
        // 올인젝터 방식으로: 메인 인벤만 직접 스캔
        // CharacterMainControl.Main → CharacterItem → Inventory → foreach(Item)
        // ─────────────────────────────────────
        private bool ScanMainInventoryForDice(CharacterMainControl player)
        {
            if (player == null)
                return false;

            Item characterItem = player.CharacterItem;
            if (characterItem == null)
            {
                Debug.Log("[MutatorDice] CharacterItem == null");
                return false;
            }

            object inventoryObj;
            try
            {
                inventoryObj = characterItem.Inventory;
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] CharacterItem.Inventory 예외: " + ex);
                return false;
            }

            if (inventoryObj == null)
            {
                Debug.Log("[MutatorDice] CharacterItem.Inventory == null");
                return false;
            }

            IEnumerable inventoryEnum = inventoryObj as IEnumerable;
            if (inventoryEnum == null)
            {
                Debug.Log("[MutatorDice] Inventory를 IEnumerable로 캐스팅 실패: " +
                          inventoryObj.GetType().FullName);
                return false;
            }

            EnsureItemFields();

            int count = 0;
            int logged = 0;

            foreach (object obj in inventoryEnum)
            {
                if (obj == null)
                    continue;

                Item item = obj as Item;
                if (item == null)
                    continue;

                count++;

                if (IsDiceItem(item))
                {
                    Debug.Log("[MutatorDice] 인벤에서 주사위 발견");
                    return true;
                }

                if (logged < 8)
                {
                    int id = -1;
                    string name = null;

                    if (_itemTypeIdField != null)
                    {
                        try
                        {
                            object raw = _itemTypeIdField.GetValue(item);
                            if (raw != null)
                                id = (int)raw;
                        }
                        catch
                        {
                        }
                    }

                    if (_itemDisplayNameField != null)
                    {
                        try
                        {
                            object rawName = _itemDisplayNameField.GetValue(item);
                            if (rawName != null)
                                name = rawName as string;
                        }
                        catch
                        {
                        }
                    }

                    Debug.Log("[MutatorDice] 인벤 아이템[" + logged + "]: id=" + id +
                              ", name=" + (string.IsNullOrEmpty(name) ? "(null)" : name));
                    logged++;
                }
            }

            Debug.Log("[MutatorDice] 인벤 스캔(메인): count=" + count);
            return false;
        }

        private static void EnsureItemFields()
        {
            if (_itemFieldsResolved)
                return;

            _itemFieldsResolved = true;

            try
            {
                BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _itemTypeIdField = typeof(Item).GetField("typeID", flags);
                _itemDisplayNameField = typeof(Item).GetField("displayName", flags);

                if (_itemTypeIdField == null)
                {
                    Debug.Log("[MutatorDice] Item.typeID 필드를 찾지 못했습니다.");
                }
                if (_itemDisplayNameField == null)
                {
                    Debug.Log("[MutatorDice] Item.displayName 필드를 찾지 못했습니다.");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] EnsureItemFields 예외: " + ex);
            }
        }

        private static bool IsDiceItem(Item item)
        {
            EnsureItemFields();

            int id = -1;
            string name = null;

            if (_itemTypeIdField != null)
            {
                try
                {
                    object raw = _itemTypeIdField.GetValue(item);
                    if (raw != null)
                        id = (int)raw;
                }
                catch
                {
                }
            }

            if (_itemDisplayNameField != null)
            {
                try
                {
                    object rawName = _itemDisplayNameField.GetValue(item);
                    if (rawName != null)
                        name = rawName as string;
                }
                catch
                {
                }
            }

            bool byId = (id == SilverDiceId || id == GoldDiceId);

            bool byName = false;
            if (!string.IsNullOrEmpty(name))
            {
                string lower = name.ToLowerInvariant();
                for (int i = 0; i < DiceNameKeywords.Length; i++)
                {
                    if (lower.Contains(DiceNameKeywords[i]))
                    {
                        byName = true;
                        break;
                    }
                }
            }

            if (byId || byName)
            {
                Debug.Log(string.Format(
                    "[MutatorDice] 주사위 아이템 감지: id={0}, name={1}, byId={2}, byName={3}",
                    id,
                    string.IsNullOrEmpty(name) ? "(null)" : name,
                    byId,
                    byName));
                return true;
            }

            return false;
        }
    }

    public class DiceBuffHUD : MonoBehaviour
    {
        private static DiceBuffHUD _instance;
        public static DiceBuffHUD Instance
        {
            get { return _instance; }
        }

        private GUIStyle _style;
        private bool _styleInitialized;

        // 말풍선 배경용 하얀 텍스처
        private Texture2D _bgTex;

        private int _lastRoll;
        private float _lastMoveMul;
        private float _lastTakenMul;
        private float _lastDealtMul;

        private float _showUntilTime;
        private const float ShowDuration = 3.0f;

        private Camera _camera;
        private Transform _playerTransform;

        // ➜ 어떤 언어 브랜치를 썼는지 한 번만 로그 찍기
        private bool _langLogged;
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.Log("[MutatorDice] DiceBuffHUD.Awake - 기존 인스턴스 제거");
                Destroy(this.gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(this.gameObject);

            Debug.Log("[MutatorDice] DiceBuffHUD.Awake 완료");
        }

        private void OnEnable()
        {
            Debug.Log("[MutatorDice] DiceBuffHUD.OnEnable");
        }

        private void Update()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
            }

            if (_playerTransform == null && DiceBuffController.Instance != null)
            {
                CharacterMainControl p = DiceBuffController.Instance.GetPlayer();
                if (p != null)
                {
                    _playerTransform = p.transform;
                    Debug.Log("[MutatorDice] DiceBuffHUD - 플레이어 Transform 연결");
                }
            }
        }

        public void OnDiceRoll(int roll, float moveMul, float dmgTakenMul, float dmgDealtMul)
        {
            _lastRoll = roll;
            _lastMoveMul = moveMul;
            _lastTakenMul = dmgTakenMul;
            _lastDealtMul = dmgDealtMul;
            _showUntilTime = Time.time + ShowDuration;
        }

        private void EnsureStyle()
        {
            if (_styleInitialized)
                return;

            _style = new GUIStyle(GUI.skin.box);
            _style.fontSize = 18;
            _style.alignment = TextAnchor.MiddleCenter;
            _style.normal.textColor = Color.black;
            _style.padding = new RectOffset(8, 8, 6, 6);
            _style.wordWrap = true;

            // ───── 하얀 말풍선 배경 ─────
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.95f)); // 거의 흰색, 살짝 투명
                _bgTex.Apply();
            }
            _style.normal.background = _bgTex;

            _styleInitialized = true;
        }

        // 시스템 언어에 따라 말풍선 텍스트 생성
        // 시스템 언어에 따라 말풍선 내용 선택
        private string GetBubbleText()
        {
            SystemLanguage lang = Application.systemLanguage;

            // 디버그용: 실제로 어떤 언어 브랜치를 타는지 한 번만 로그
            if (!_langLogged)
            {
                _langLogged = true;
                Debug.Log("[MutatorDice] DiceBuffHUD.GetBubbleText - systemLanguage = " + lang);
            }

            switch (lang)
            {
                case SystemLanguage.Korean:
                    // 한국어
                    return string.Format(
                        "주사위 {0}\n이속 x{1:F2} / 받피 x{2:F2} / 공격 x{3:F2}",
                        _lastRoll, _lastMoveMul, _lastTakenMul, _lastDealtMul);

                case SystemLanguage.Japanese:
                    // 일본어
                    return string.Format(
                        "ダイス {0}\n移動速度 x{1:F2} / 被ダメージ x{2:F2} / 与ダメージ x{3:F2}",
                        _lastRoll, _lastMoveMul, _lastTakenMul, _lastDealtMul);

                default:
                    // 그 외 → 영어
                    return string.Format(
                        "Dice {0}\nMove x{1:F2} / Dmg taken x{2:F2} / Dmg dealt x{3:F2}",
                        _lastRoll, _lastMoveMul, _lastTakenMul, _lastDealtMul);
            }
        }


        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint &&
                Event.current.type != EventType.Layout)
                return;

            if (Time.time > _showUntilTime)
                return;

            if (_camera == null || _playerTransform == null)
                return;

            EnsureStyle();

            Vector3 worldPos = _playerTransform.position + Vector3.up * 2.2f;
            Vector3 screenPos = _camera.WorldToScreenPoint(worldPos);
            if (screenPos.z < 0f)
                return;

            float width = 260f;
            float height = 70f;
            float x = screenPos.x - width / 2f;
            float y = Screen.height - screenPos.y - height - 20f;

            Rect rect = new Rect(x, y, width, height);

            string text = GetBubbleText();
            GUI.Box(rect, text, _style);
        }
    }


    internal static class DiceDamagePatch
    {
        private static bool _patched;

        public static void ApplyPatches()
        {
            if (_patched)
                return;

            try
            {
                Type healthType = null;

                try
                {
                    // CharacterMainControl 이 들어있는 어셈블리에서 Health 타입 찾기
                    System.Reflection.Assembly asm = typeof(CharacterMainControl).Assembly;

                    healthType = asm.GetType("Health");
                    if (healthType == null)
                    {
                        Type[] types = asm.GetTypes();
                        for (int i = 0; i < types.Length; i++)
                        {
                            Type t = types[i];
                            if (t.Name == "Health")
                            {
                                healthType = t;
                                break;
                            }
                        }
                    }
                }
                catch (Exception exFind)
                {
                    Debug.Log("[MutatorDice] DiceDamagePatch.ApplyPatches HealthType 탐색 예외: " + exFind);
                }

                if (healthType == null)
                {
                    Debug.Log("[MutatorDice] DiceDamagePatch.ApplyPatches - HealthType 없음, 패치 생략");
                    return;
                }

                HarmonyLib.Harmony harmony =
                    new HarmonyLib.Harmony("dicebuff.mutator");

                BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo[] methods = healthType.GetMethods(flags);

                int patchedCount = 0;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];

                    if (m.ReturnType != typeof(void))
                        continue;

                    if (!m.Name.Contains("Damage"))
                        continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 0)
                        continue;

                    if (ps[0].ParameterType != typeof(float))
                        continue;

                    HarmonyLib.HarmonyMethod prefix =
                        new HarmonyLib.HarmonyMethod(
                            typeof(DiceDamagePatch).GetMethod(
                                "Prefix",
                                BindingFlags.Static | BindingFlags.NonPublic));

                    harmony.Patch(m, prefix: prefix);
                    patchedCount++;
                }

                Debug.Log("[MutatorDice] Health 데미지 메서드 패치 수: " + patchedCount);
                _patched = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] DiceDamagePatch.ApplyPatches 예외: " + ex);
            }
        }

        private static void Prefix(object __instance, ref float __0)
        {
            try
            {
                if (!DiceBuffController.HasDice)
                    return;

                float mul;

                object playerHealth = DiceBuffController.PlayerHealth;

                if (playerHealth != null && object.ReferenceEquals(__instance, playerHealth))
                {
                    // 플레이어가 맞는 데미지 → 방어력 배율 적용
                    mul = DiceBuffController.DamageTakenMultiplier;
                }
                else
                {
                    // 적이 맞는 데미지 → 공격력 배율 적용
                    mul = DiceBuffController.DamageDealtMultiplier;
                }

                if (Math.Abs(mul - 1f) < 0.001f)
                    return;

                __0 *= mul;
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] DiceDamagePatch.Prefix 예외: " + ex);
            }
        }
    }

    /// <summary>
    /// MapLevel(ModBehaviour.cs)을 리플렉션으로 찾아서
    /// 주사위 눈에 따라 SetDifficultyWithArmorCheck를 호출하는 브리지.
    /// MapLevel 모드가 없으면 그냥 아무 것도 안 함.
    /// </summary>
    internal static class MapLevelBridge
    {
        private static bool _initialized;
        private static bool _available;

        private static Type _mapLevelModType;
        private static Type _mapDifficultyEnumType;
        private static MethodInfo _setDifficultyMethod;

        public static void TryInitialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                // 1) MapLevel.ModBehaviour 타입 찾기
                System.Reflection.Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    System.Reflection.Assembly asm = asms[i];
                    Type t = asm.GetType("MapLevel.ModBehaviour");
                    if (t != null)
                    {
                        _mapLevelModType = t;
                        break;
                    }
                }

                if (_mapLevelModType == null)
                {
                    Debug.Log("[MutatorDice] MapLevelBridge - MapLevel.ModBehaviour 타입을 찾지 못함 (MapLevel 모드 미설치?)");
                    return;
                }

                // 2) MapLevel.MapDifficulty enum 타입 찾기
                _mapDifficultyEnumType = _mapLevelModType.Assembly.GetType("MapLevel.MapDifficulty");
                if (_mapDifficultyEnumType == null)
                {
                    Debug.Log("[MutatorDice] MapLevelBridge - MapLevel.MapDifficulty 타입을 찾지 못함");
                    return;
                }

                // 3) private bool SetDifficultyWithArmorCheck(MapDifficulty newDifficulty)
                _setDifficultyMethod = _mapLevelModType.GetMethod(
                    "SetDifficultyWithArmorCheck",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (_setDifficultyMethod == null)
                {
                    Debug.Log("[MutatorDice] MapLevelBridge - SetDifficultyWithArmorCheck 메서드를 찾지 못함");
                    return;
                }

                _available = true;
                Debug.Log("[MutatorDice] MapLevelBridge - MapLevel 연동 준비 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] MapLevelBridge.TryInitialize 예외: " + ex);
            }
        }

        private static object GetMapLevelInstance()
        {
            if (_mapLevelModType == null)
                return null;

            try
            {
                // public static ModBehaviour Instance { get; }
                PropertyInfo prop = _mapLevelModType.GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static);

                if (prop != null)
                {
                    object inst = prop.GetValue(null, null);
                    return inst;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] MapLevelBridge.GetMapLevelInstance 예외: " + ex);
            }

            return null;
        }

        private static string GetDifficultyNameForRoll(int roll)
        {
            // MapLevel.MapDifficulty enum 이름 기준:
            // Normal, Confidential, TopSecret, FinalNight
            switch (roll)
            {
                case 1:
                case 2:
                    return "Normal";

                case 3:
                case 4:
                    return "Confidential";

                case 5:
                    return "TopSecret";

                case 6:
                    return "FinalNight";

                default:
                    return null;
            }
        }

        public static void ApplyDiceRollToMap(int roll)
        {
            try
            {
                if (!_initialized)
                {
                    TryInitialize();
                }

                if (!_available)
                    return;

                string diffName = GetDifficultyNameForRoll(roll);
                if (string.IsNullOrEmpty(diffName))
                    return;

                object mapLevelInstance = GetMapLevelInstance();
                if (mapLevelInstance == null)
                {
                    Debug.Log("[MutatorDice] MapLevelBridge - Instance를 찾지 못함");
                    return;
                }

                object diffEnum;
                try
                {
                    diffEnum = Enum.Parse(_mapDifficultyEnumType, diffName);
                }
                catch
                {
                    Debug.Log("[MutatorDice] MapLevelBridge - Enum.Parse 실패: " + diffName);
                    return;
                }

                object[] args = new object[] { diffEnum };
                bool result = false;

                try
                {
                    object ret = _setDifficultyMethod.Invoke(mapLevelInstance, args);
                    if (ret is bool b)
                    {
                        result = b;
                    }
                }
                catch (Exception exInvoke)
                {
                    Debug.Log("[MutatorDice] MapLevelBridge.ApplyDiceRollToMap Invoke 예외: " + exInvoke);
                    return;
                }

                Debug.Log(string.Format(
                    "[MutatorDice] MapLevelBridge - 주사위 {0} => 난이도 {1} 적용 시도 (성공여부={2})",
                    roll, diffName, result));
            }
            catch (Exception ex)
            {
                Debug.Log("[MutatorDice] MapLevelBridge.ApplyDiceRollToMap 예외: " + ex);
            }
        }
    }
}
