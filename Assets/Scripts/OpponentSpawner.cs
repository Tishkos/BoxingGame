using RootMotion.Dynamics;
using UnityEngine;

/// <summary>
/// Puts a second fighter in front of you. By default it CLONES the player's boxer, so the opponent is the same
/// character with the same rig, the same pose library, the same Legs Animator and the same punch system — no
/// second prefab to build and keep in sync, and anything you improve about the player improves him too.
///
/// The clone is then converted: player-only components come off (camera follow, the spawner itself, the
/// player's hit feedback), a <see cref="BoxerHealth"/> and a <see cref="BoxerAI"/> go on, and the AI takes the
/// controls through <see cref="AIBoxerInput"/>. The player gets a BoxerHealth too, because a fight only works
/// if it goes both ways.
///
/// THE PUPPET COMES TOO. The player's PuppetMaster ragdoll lives OUTSIDE the character hierarchy, so cloning
/// the character alone used to leave the opponent without a physical body — no real gloves, and a "knockout"
/// where he just froze standing up. The spawner now clones the puppet as well, retargets every muscle onto the
/// clone's bones by path, and hands it to the clone's <see cref="BoxerPhysics"/> — so the opponent blocks,
/// staggers and goes DOWN exactly like the player.
///
/// Put this on the player's boxer (or run Tools ▸ Boxer ▸ Fight ▸ Setup AI Opponent) and press Play.
/// </summary>
public class OpponentSpawner : MonoBehaviour
{
    [Header("Who")]
    [Tooltip("The player's boxer. Left empty, this component's own GameObject is used.")]
    [SerializeField] private BoxerPunchController playerBoxer;

    [Tooltip("Optional. A prefab to spawn instead of cloning the player — use this once the opponent should " +
             "look like someone else.")]
    [SerializeField] private GameObject opponentPrefab;

    [Header("Where")]
    [Tooltip("How far in front of the player he appears (metres).")]
    [Min(0.5f)] [SerializeField] private float spawnDistance = 2.2f;

    [Tooltip("Spawn as soon as the scene starts.")]
    [SerializeField] private bool spawnOnStart = true;

    [Tooltip("Press this key to spawn another / respawn him. None = disabled.")]
    [SerializeField] private KeyCode spawnKey = KeyCode.P;

    [Header("The opponent")]
    [Range(0f, 1f)] [SerializeField] private float skill = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float aggression = 0.55f;
    [SerializeField] private string opponentName = "Opponent";

    [Tooltip("Tint the opponent so you can tell the two of you apart at a glance. Clear = leave him alone.")]
    [SerializeField] private Color tint = new Color(0.75f, 0.35f, 0.3f, 1f);

    /// <summary>The fighter currently in there with you, if any.</summary>
    public BoxerAI Current { get; private set; }

    private GameObject currentPuppet;   // the opponent's cloned ragdoll — a separate root, destroyed with him

    private void Start()
    {
        if (playerBoxer == null) playerBoxer = GetComponent<BoxerPunchController>();
        if (playerBoxer == null) playerBoxer = FindAnyObjectByType<BoxerPunchController>();
        if (playerBoxer == null)
        {
            Debug.LogError("OpponentSpawner: no BoxerPunchController to fight — assign Player Boxer.", this);
            enabled = false;
            return;
        }

        // A fight goes both ways: the player has to be hittable too.
        if (playerBoxer.GetComponent<BoxerHealth>() == null)
            playerBoxer.gameObject.AddComponent<BoxerHealth>();

        if (spawnOnStart) Spawn();
    }

    private void Update()
    {
        if (spawnKey == KeyCode.None) return;
        UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        // Input System has no KeyCode lookup; P is the one that matters and is cheap to special-case.
        if (spawnKey == KeyCode.P && kb.pKey.wasPressedThisFrame) Spawn();
    }

    /// <summary>Put a fresh opponent in front of the player, replacing whoever was there.</summary>
    [ContextMenu("Spawn Opponent")]
    public void Spawn()
    {
        if (playerBoxer == null) return;
        if (Current != null) Destroy(Current.gameObject);
        if (currentPuppet != null) { Destroy(currentPuppet); currentPuppet = null; }

        Transform player = playerBoxer.transform;
        Vector3 where = player.position + player.forward * spawnDistance;
        Quaternion facing = Quaternion.LookRotation(-player.forward, Vector3.up);

        GameObject clone;
        if (opponentPrefab != null)
        {
            clone = Instantiate(opponentPrefab, where, facing);
        }
        else
        {
            // Cloning a live character copies its current pose too, which is fine — the Animator re-poses it on
            // the first frame. Disabled during construction so nothing runs Awake against a half-built fighter.
            bool wasActive = player.gameObject.activeSelf;
            player.gameObject.SetActive(false);
            clone = Instantiate(player.gameObject, where, facing);
            player.gameObject.SetActive(wasActive);
        }

        clone.name = opponentName;
        Convert(clone);

        // Wake-up order matters: the FIGHTER first (his BoxerPhysics already holds the puppet reference and
        // waits for it to initiate), THEN the puppet — which collects its solvers and pins its muscles from the
        // now-live clone. The other way round, PuppetMaster would initiate against an inactive character.
        clone.SetActive(true);
        if (currentPuppet != null) currentPuppet.SetActive(true);

        Current = clone.GetComponent<BoxerAI>();
        Debug.Log($"Opponent '{clone.name}' is in{(currentPuppet != null ? " (with his own physical body)" : " (IK body)")}. " +
                  $"Skill {skill:0.00}, aggression {aggression:0.00}. Knock him down three times and he stays down.", clone);
    }

    /// <summary>Strip the player-only parts off the clone and give it a brain.</summary>
    private void Convert(GameObject clone)
    {
        // Anything that assumes it is looking at, or listening for, the human.
        foreach (OpponentSpawner s in clone.GetComponentsInChildren<OpponentSpawner>(true)) Destroy(s);
        foreach (CameraFollow c in clone.GetComponentsInChildren<CameraFollow>(true)) Destroy(c);
        foreach (ImpactFeedback f in clone.GetComponentsInChildren<ImpactFeedback>(true)) Destroy(f);
        foreach (Camera c in clone.GetComponentsInChildren<Camera>(true)) Destroy(c.gameObject);
        foreach (AudioListener a in clone.GetComponentsInChildren<AudioListener>(true)) Destroy(a);

        BoxerHealth health = clone.GetComponent<BoxerHealth>();
        if (health == null) health = clone.AddComponent<BoxerHealth>();

        BoxerAI ai = clone.GetComponent<BoxerAI>();
        if (ai == null) ai = clone.AddComponent<BoxerAI>();
        ai.Configure(playerBoxer.transform, skill, aggression);

        // The clone inherited the PLAYER's serialized puppet reference (cross-hierarchy references survive
        // cloning) — cleared first, or two fighters would drive one ragdoll. His own puppet arrives below.
        BoxerPhysics clonePhysics = clone.GetComponent<BoxerPhysics>();
        if (clonePhysics != null) clonePhysics.SetPuppet(null);

        currentPuppet = ClonePuppet(clone);

        // The player should square up to HIM, not to the bag, the moment he exists.
        Controller playerMove = playerBoxer.GetComponent<Controller>();
        if (playerMove != null) playerMove.SetTarget(clone.transform);

        if (tint.a > 0f) Tint(clone);
    }

    /// <summary>
    /// Give the opponent a physical body of his own: clone the player's PuppetMaster (it lives OUTSIDE the
    /// character hierarchy, so the character clone alone never had one), retarget every muscle onto the clone's
    /// bones by hierarchy path, move it to the spawn spot, and hand it to the clone's <see cref="BoxerPhysics"/>.
    /// Returned INACTIVE — it is switched on after the fighter, so it initiates against a live character.
    /// Returns null (IK fallback, a warning, nothing broken) if any step cannot complete.
    /// </summary>
    private GameObject ClonePuppet(GameObject clone)
    {
        PuppetMaster source = null;
        foreach (PuppetMaster pm in FindObjectsByType<PuppetMaster>(FindObjectsSortMode.None))
            if (pm.targetRoot == playerBoxer.transform) { source = pm; break; }
        if (source == null) return null;   // the player is IK-only; so is the opponent

        // Instantiate while the source is briefly inactive so the clone's Awake cannot run half-retargeted.
        GameObject puppetClone;
        bool wasActive = source.gameObject.activeSelf;
        try
        {
            source.gameObject.SetActive(false);
            puppetClone = Instantiate(source.gameObject);
        }
        finally
        {
            source.gameObject.SetActive(wasActive);
        }

        puppetClone.name = clone.name + " Puppet";
        PuppetMaster puppet = puppetClone.GetComponent<PuppetMaster>();
        if (puppet == null || puppet.muscles == null)
        {
            Destroy(puppetClone);
            return null;
        }

        puppet.targetRoot = clone.transform;   // solvers and the animator are collected from here at initiation

        // Every muscle chased a PLAYER bone; point it at the same bone on the clone (identical hierarchy).
        int missed = 0;
        foreach (Muscle muscle in puppet.muscles)
        {
            if (muscle == null || muscle.target == null) continue;
            string path = PathBetween(playerBoxer.transform, muscle.target);
            Transform mapped = path == null ? null : (path.Length == 0 ? clone.transform : clone.transform.Find(path));
            if (mapped != null) muscle.target = mapped;
            else missed++;
        }
        if (missed > 0)
        {
            Debug.LogWarning($"OpponentSpawner: {missed} puppet muscle(s) could not be retargeted — the opponent stays IK-bodied.", this);
            Destroy(puppetClone);
            return null;
        }

        // The cloned bodies are still standing where the PLAYER is; carry them to the spawn spot before waking.
        Vector3 playerPos = playerBoxer.transform.position;
        Quaternion delta = clone.transform.rotation * Quaternion.Inverse(playerBoxer.transform.rotation);
        puppetClone.transform.SetPositionAndRotation(
            clone.transform.position + delta * (puppetClone.transform.position - playerPos),
            delta * puppetClone.transform.rotation);

        BoxerPhysics physics = clone.GetComponent<BoxerPhysics>();
        if (physics != null) physics.SetPuppet(puppet);

        return puppetClone;   // inactive; Spawn() wakes it right after the fighter
    }

    /// <summary>Hierarchy path from <paramref name="root"/> down to <paramref name="leaf"/> ("" = the root itself, null = not under it).</summary>
    private static string PathBetween(Transform root, Transform leaf)
    {
        if (leaf == root) return "";
        System.Text.StringBuilder path = new System.Text.StringBuilder(leaf.name);
        Transform t = leaf.parent;
        while (t != null && t != root)
        {
            path.Insert(0, t.name + "/");
            t = t.parent;
        }
        return t == root ? path.ToString() : null;
    }

    /// <summary>Give him his own colour so the two identical fighters are readable in a scramble.</summary>
    private void Tint(GameObject clone)
    {
        foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
        {
            Material[] materials = r.materials;   // instances, so the player's own look is untouched
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                if (materials[i].HasProperty("_BaseColor")) materials[i].SetColor("_BaseColor", materials[i].GetColor("_BaseColor") * tint);
                else if (materials[i].HasProperty("_Color")) materials[i].SetColor("_Color", materials[i].GetColor("_Color") * tint);
            }
            r.materials = materials;
        }
    }
}
