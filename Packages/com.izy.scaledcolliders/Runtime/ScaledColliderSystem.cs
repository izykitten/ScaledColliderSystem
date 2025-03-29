using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Prismic247.ScaledColliders {
    // Move the enum outside the class for better UdonSharp compatibility
    public enum ScaleSelectionMode {
        ParentObject = 0,
        ObjectsList = 1
    }

    [DefaultExecutionOrder(-1), UdonBehaviourSyncMode(BehaviourSyncMode.None), DisallowMultipleComponent, RequireComponent(typeof(CapsuleCollider)), RequireComponent(typeof(Rigidbody))]
    public class ScaledColliderSystem : UdonSharpBehaviour {
        [Header("Scaled Collider System")]
        [Tooltip("Enables/disables the scaled collider system.\n\nCall 'ToggleColliders(bool)' instead if changing this during runtime.\n\nPrefab can be found at https://github.com/Prismic247/ScaledColliderSystem/")]
        public bool enableScaledColliders = true;
        
        [SerializeField]
        [Tooltip("How to select objects for scaling - either by parent object or by list")]
        public ScaleSelectionMode selectionMode = ScaleSelectionMode.ParentObject;
        
        [Tooltip("The game object parent which contains the parts of the world intended to be colliably scaled.\n\nFor performance reasons only include collidable objects that need to be scaled.\n\nDo not use this prefab as the parent object.")]
        public GameObject worldParent;
        
        [Header("Objects With Children")]
        [Tooltip("List of game objects to scale including all their children")]
        public GameObject[] objects;
        
        [Header("Objects Without Children")]
        [Tooltip("List of game objects to scale only themselves, ignoring all their children")]
        public GameObject[] objectsNoChildren;
        
        [Header("Collider Options")]
        [Tooltip("List of game objects whose original colliders should remain active (not just their scaled copies)")]
        public GameObject[] objectsKeepColliders;
        
        [Tooltip("Synchronize enabled state of original colliders with their virtual copies for objects in the keepColliders list")]
        public bool syncColliderStates = true;
        
        [Tooltip("The player eye height in meters where the relative world scale should be considerd 1-to-1. Everything scales relative to this value.\n\nDefault: 1.6")]
        [Range(0.3f, 20f)]
        public float baseEyeHeight = 1.6f;
        [Header("Player Size Settings")]
        [Tooltip("Allow players to manually set their own scale, bounded by 'Minimum Scale' and 'Maximum Scale'. Reapplies on join and on respawn.")]
        public bool manualScalingAllowed = true;
        [Tooltip("The smallest eye height in meters that players can manually set their scale to, controlled by 'Manual Scaling Allowed'. Reapplies on join and on respawn.\n\nWARNING: Lower scales cause some degree of collision instability, which gets worse the smaller you go. Recommended minimum is 0.3m.\n\nDefault: 0.3")]
        [Range(0.3f, 20f)]
        public float minimumEyeHeight = 0.3f;
        [Tooltip("The largest eye height in meters that players can manually set their scale to, controlled by 'Manual Scaling Allowed'. Reapplies on join and on respawn.\n\nDefault: 5")]
        [Range(0.3f, 20f)]
        public float maximumEyeHeight = 5f;
        [Header("Player Movement Settings")]
        [Tooltip("Enables/disables whether or not movement (walking, jumping, fall speed, etc) should be scaled relative to the player scale.")]
        public bool enableScaledMovement = true;
        [Tooltip("The player walking speed, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 2")]
        public float baseWalkSpeed = 2;
        [Tooltip("The player run speed, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 4")]
        public float baseRunSpeed = 4;
        [Tooltip("The player strafing speed, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 2")]
        public float baseStrafeSpeed = 2;
        [Tooltip("The player jump impulse, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 3")]
        public float baseJumpImpulse = 3;
        [Tooltip("The player gravity, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 1")]
        public float basePlayerGravity = 1;
        [Header("Player Sound Settings")]
        [Tooltip("Enables/disables whether or not sounds from the player (voice and avatar audio) should be scaled relative to the player scale.")]
        public bool enableScaledSounds = true;
        [Tooltip("How many meters away that voices can be heard, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 25")]
        public float baseVoiceDistance = 25;
        [Tooltip("How many meters away that avatar audio can be heard, which will be scaled based on the player's current eye height relative to 'Base Player Height'.\n\nDefault: 40")]
        public float baseAvatarAudioDistance = 40;
        [Header("Debug Options")]
        [Tooltip("Displays a material specified on 'Ghost Material' on each collider object that usually has a mesh, for debugging.\n\nCall 'ToggleGhosts(bool)' instead if changing this during runtime.")]
        public bool showColliderGhosts = false;
        [Tooltip("A ghost material to show on the invisible colliders, used with 'Show Collider Ghosts' for debugging.\n\nIf used, recommended is a semi-transparent material.")]
        public Material ghostMaterial;

        [Header("Stabilization Settings")]
        [Tooltip("Smooths out vertical movement of colliders to prevent jittering")]
        public bool stabilizeVerticalMovement = true;

        [Tooltip("Strength of vertical stabilization (higher values = more stable but potentially less responsive)")]
        [Range(0.1f, 10f)]
        public float verticalStabilizationStrength = 3f;

        private GameObject virtualWorld;
        private VRCPlayerApi local;
        private Vector3 playerPosLast = Vector3.zero, playerVelLast = Vector3.zero;
        private Quaternion playerRotLast = Quaternion.identity;
        private Rigidbody rigidBody;
        private int collisionMask = 0b10000000000;
        private float playerScale = 1, worldScale = 1;

        // Initialize these arrays to prevent UdonSharp serialization issues
        [SerializeField, HideInInspector] 
        private GameObject[] taggedObjects = new GameObject[0];
        
        [SerializeField, HideInInspector] 
        private GameObject[] virtualObjects = new GameObject[0];
        
        private GameObject virtualObjectsContainer; // Container for all virtual objects

        // Add variables to track positions
        private Vector3 lastVirtualWorldPos = Vector3.zero;
        private Vector3 targetWorldPos = Vector3.zero;
        private float verticalVelocity = 0f;

        // These arrays will track the state of original colliders
        // We can't use actual dictionaries in UdonSharp, so we'll use parallel arrays
        [HideInInspector, SerializeField] 
        private GameObject[] stateTrackedObjects = new GameObject[0];
        
        [HideInInspector, SerializeField]
        private bool[] stateWasEnabled = new bool[0];

        void Start() {
            local = Networking.LocalPlayer;
            if (local == null) { return; }
            local.SetManualAvatarScalingAllowed(manualScalingAllowed);
            local.SetAvatarEyeHeightMinimumByMeters(minimumEyeHeight);
            
            local.SetAvatarEyeHeightMaximumByMeters(maximumEyeHeight);
            SetLocalMovement();
            rigidBody = gameObject.GetComponent<Rigidbody>();
            rigidBody.useGravity = false;
            rigidBody.rotation = Quaternion.identity;
            rigidBody.freezeRotation = true;
            CapsuleCollider capsuleCollider = gameObject.GetComponent<CapsuleCollider>();
            capsuleCollider.height = 1.6f;
            capsuleCollider.radius = 0.2f;
            capsuleCollider.center = new Vector3(0, 0.8f, 0);
            InitializeScaledColliders();
        }
        
        public override void OnPlayerJoined(VRCPlayerApi player) {
            if (player == null) {
                return;
            } else if (player.isLocal) {
                player.SetManualAvatarScalingAllowed(manualScalingAllowed);
                player.SetAvatarEyeHeightMinimumByMeters(minimumEyeHeight);
                player.SetAvatarEyeHeightMaximumByMeters(maximumEyeHeight);
                AdjustLocalScale();
            } else {
                AdjustRemoteScale(player);
            }
        }

        public override void OnPlayerRespawn(VRCPlayerApi player) {
            if (player == null) {
                return;
            } else if (player.isLocal) {
                player.SetManualAvatarScalingAllowed(manualScalingAllowed);
                player.SetAvatarEyeHeightMinimumByMeters(minimumEyeHeight);
                player.SetAvatarEyeHeightMaximumByMeters(maximumEyeHeight);
                AdjustLocalScale();
            } else {
                AdjustRemoteScale(player);
            }
        }

        public void FixedUpdate() {
            if (local == null || !local.IsValid() || !enableScaledColliders) { return; }
            #if UNITY_EDITOR
            AdjustLocalScale();
            #endif
            playerPosLast = local.GetPosition();
            playerRotLast = local.GetRotation();
            playerVelLast = local.GetVelocity();
            
            if (selectionMode == ScaleSelectionMode.ParentObject) {
                if (virtualWorld == null || worldParent == null) return;
                MoveAround(worldParent, virtualWorld, playerPosLast);
                
                // Always sync object states
                SyncObjectStates(worldParent, virtualWorld);
                
                // Synchronize collider states if enabled
                if (syncColliderStates && objectsKeepColliders != null && objectsKeepColliders.Length > 0) {
                    SyncColliderStates(worldParent, virtualWorld);
                }
            } else {
                if (taggedObjects == null || virtualObjects == null) return;
                
                for (int i = 0; i < taggedObjects.Length; i++) {
                    if (i >= virtualObjects.Length) {
                        continue;
                    }
                    GameObject to = taggedObjects[i];
                    GameObject vo = virtualObjects[i];
                    if (to == null || vo == null) {
                        continue;
                    }

                    MoveAround(to, vo, playerPosLast);
                    
                    // Always sync object states
                    SyncObjectStates(to, vo);
                    
                    // Synchronize collider states if enabled and in keep list
                    if (syncColliderStates && ShouldKeepColliders(to)) {
                        SyncColliderStates(to, vo);
                    }
                }
            }
            
            rigidBody.MovePosition(playerPosLast);
        }

        public void OnCollisionEnter(Collision collision) {
            if (playerScale < 1) {InterceptMovement(collision);}
        }

        private void InterceptMovement(Collision collision) {
            local.TeleportTo((playerPosLast + local.GetPosition()) / 2, local.GetRotation());
            local.SetVelocity(RemoveComponent(local.GetVelocity(), collision.impulse));
        }

        public void OnCollisionExit(Collision collision) {
            if (playerScale < 1) {DampenMovement();}
        }

        private void DampenMovement() {
            Vector3 currVel = playerVelLast;
            float horzMag = Mathf.Sqrt(currVel.x * currVel.x + currVel.z * currVel.z);
            float scaledSpeed = local.GetWalkSpeed();
            if (horzMag > scaledSpeed) {
                local.TeleportTo((playerPosLast + local.GetPosition()) / 2, playerRotLast);
                Vector3 relVel = new Vector3(currVel.x / horzMag * scaledSpeed, currVel.y, currVel.z / horzMag * scaledSpeed);
                local.SetVelocity(relVel);
            }
        }

        private Vector3 RemoveComponent(Vector3 vector, Vector3 direction) {
            direction = direction.normalized;
            return vector - direction * Vector3.Dot(vector, direction);
        }

        public override void OnAvatarChanged(VRCPlayerApi player) {
            if (player == null) {
                return;
            } else if (player.isLocal) {
                AdjustLocalScale();
            } else {
                AdjustRemoteScale(player);
            }
        }

        public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters) {
            if (player == null) {
                return;
            } else if (player.isLocal) {
                AdjustLocalScale();
            } else {
                AdjustRemoteScale(player);
            }
        }

        public float GetLocalPlayerScale() {return playerScale;}
        
        public float GetPlayerScale(VRCPlayerApi player) {return player.GetAvatarEyeHeightAsMeters() / baseEyeHeight;}

        private void AdjustLocalScale() {
            playerScale = local.GetAvatarEyeHeightAsMeters() / baseEyeHeight;
            worldScale = 1 / playerScale;
            SetLocalMovement();
            if (!enableScaledColliders) { return; }
            
            if (selectionMode == ScaleSelectionMode.ParentObject) {
                if (worldParent == null || virtualWorld == null) return;
                virtualWorld.transform.localScale = new Vector3(worldScale, worldScale, worldScale);
                MoveAround(worldParent, virtualWorld, local.GetPosition());
            } else {
                if (virtualObjects == null) return;
                
                for (int i = 0; i < virtualObjects.Length; i++) {
                    if (virtualObjects[i] != null) {
                        virtualObjects[i].transform.localScale = new Vector3(worldScale, worldScale, worldScale);
                        if (i < taggedObjects.Length && taggedObjects[i] != null) {
                            MoveAround(taggedObjects[i], virtualObjects[i], local.GetPosition());
                        }
                    }
                }
            }

            // Reset stabilization variables when scale changes
            if (stabilizeVerticalMovement) {
                lastVirtualWorldPos = Vector3.zero;
                targetWorldPos = Vector3.zero;
                verticalVelocity = 0f;
            }
        }

        private void AdjustRemoteScale(VRCPlayerApi player) {
            float remoteScale = player.GetAvatarEyeHeightAsMeters() / baseEyeHeight;
            if (enableScaledSounds) {
                player.SetVoiceDistanceFar(remoteScale * baseVoiceDistance);
                player.SetAvatarAudioFarRadius(remoteScale * baseAvatarAudioDistance);
            }
        }

        private void SetLocalMovement() {
            float scale = enableScaledMovement ? playerScale : 1;
            local.SetWalkSpeed(baseWalkSpeed * scale);
            local.SetRunSpeed(baseRunSpeed * scale);
            local.SetStrafeSpeed(baseStrafeSpeed * scale);
            local.SetJumpImpulse(baseJumpImpulse * scale);
            local.SetGravityStrength(basePlayerGravity * scale);
        }

        private void MoveAround(GameObject real, GameObject target, Vector3 pivot) {
            // Use world position to ensure consistent behavior regardless of hierarchy
            Vector3 realPos = real.transform.position;
            Vector3 newPos = pivot + (realPos - pivot) * worldScale / real.transform.lossyScale.x;
            
            // Apply stabilization if enabled
            if (stabilizeVerticalMovement) {
                // Only stabilize Y axis
                if (target == virtualWorld) {
                    // Initialize positions if needed
                    if (lastVirtualWorldPos == Vector3.zero) {
                        lastVirtualWorldPos = newPos;
                        targetWorldPos = newPos;
                    }
                    
                    // Update target position
                    targetWorldPos = newPos;
                    
                    // Smooth out Y movement based on stabilization strength
                    float smoothY = Mathf.SmoothDamp(
                        lastVirtualWorldPos.y, 
                        targetWorldPos.y, 
                        ref verticalVelocity, 
                        Time.deltaTime * verticalStabilizationStrength
                    );
                    
                    // Apply smoothed Y position while keeping X and Z from direct calculation
                    newPos = new Vector3(newPos.x, smoothY, newPos.z);
                    
                    // Store for next frame
                    lastVirtualWorldPos = newPos;
                }
            }
            
            target.transform.position = newPos;
            
            // Make sure we preserve the world rotation of the original object
            target.transform.rotation = real.transform.rotation;
        }

        public bool ToggleColliderGhosts(bool state) {
            showColliderGhosts = state;
            if (enableScaledColliders) {
                InitializeScaledColliders();
            }
            return showColliderGhosts;
        }

        public bool ToggleScaledColliders(bool state) {
            bool hasValidConfig = (selectionMode == ScaleSelectionMode.ParentObject && worldParent != null) || 
                                 (selectionMode == ScaleSelectionMode.ObjectsList && 
                                  ((objects != null && objects.Length > 0) || 
                                   (objectsNoChildren != null && objectsNoChildren.Length > 0)));
            
            if (!hasValidConfig) {
                enableScaledColliders = false;
                return false;
            }
            
            enableScaledColliders = state;
            
            if (!enableScaledColliders) {
                CleanupScaledObjects();
            } else {
                InitializeScaledColliders();
            }
            return state;
        }
        
        private void CleanupScaledObjects() {
            if (selectionMode == ScaleSelectionMode.ParentObject) {
                if (virtualWorld != null && worldParent != null) {
                    virtualWorld.transform.localScale = Vector3.one;
                    virtualWorld.transform.localPosition = worldParent.transform.position;
                    PrepareWorldParent(worldParent, true);
                    Destroy(virtualWorld);
                    virtualWorld = null;
                }
            } else {
                if (taggedObjects != null && virtualObjects != null) {
                    for (int i = 0; i < taggedObjects.Length; i++) {
                        if (taggedObjects[i] != null) {
                            PrepareWorldParent(taggedObjects[i], true);
                        }
                    }
                }
                taggedObjects = null;
                virtualObjects = null;
            }
            
            // Clean up the container if it exists
            if (virtualObjectsContainer != null) {
                Destroy(virtualObjectsContainer);
                virtualObjectsContainer = null;
            }
        }

        public void InitializeScaledColliders(GameObject newWorldParent = null) {
            // Reset stabilization variables
            lastVirtualWorldPos = Vector3.zero;
            targetWorldPos = Vector3.zero;
            verticalVelocity = 0f;

            if (selectionMode == ScaleSelectionMode.ParentObject) {
                InitializeWithParent(newWorldParent);
            } else {
                InitializeWithObjectsList();
            }
        }
        
        private void InitializeWithParent(GameObject newWorldParent = null) {
            if (worldParent == null && (newWorldParent == null || newWorldParent == gameObject)) {
                enableScaledColliders = false;
                return;
            } else if (worldParent != null && newWorldParent != null && newWorldParent != gameObject) {
                PrepareWorldParent(worldParent, true);
                worldParent = newWorldParent;
            } else if (worldParent == null) {worldParent = newWorldParent;}
            
            // Clean up any existing objects first
            if (virtualWorld != null) {Destroy(virtualWorld);}
            if (virtualObjectsContainer != null) {Destroy(virtualObjectsContainer);}
            
            // Create a container for all virtual objects - using Instantiate instead of new GameObject()
            virtualObjectsContainer = Instantiate(gameObject);
            // Remove all components except Transform
            Component[] components = virtualObjectsContainer.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++) {
                string componentType = components[i].GetType().ToString();
                if (componentType != "UnityEngine.Transform") {
                    Destroy(components[i]);
                }
            }
            virtualObjectsContainer.name = "ScaledColliders_VirtualObjects";
            
            // Create the virtual world and set it as a child of the container
            virtualWorld = Instantiate(worldParent);
            virtualWorld.transform.SetParent(virtualObjectsContainer.transform, true);
            
            // Ensure rotation is preserved by explicitly setting world rotation  
            virtualWorld.transform.rotation = worldParent.transform.rotation;
            
            PrepareWorldParent(worldParent);
            PrepareVirtualWorld(virtualWorld);
            AdjustLocalScale();
        }
        
        private void InitializeWithObjectsList() {
            // First clean up any previous setup
            CleanupScaledObjects();
            
            // Check if we have any valid objects to scale
            bool hasRegularObjects = objects != null && objects.Length > 0;
            bool hasNoChildrenObjects = objectsNoChildren != null && objectsNoChildren.Length > 0;
            
            if (!hasRegularObjects && !hasNoChildrenObjects) {
                enableScaledColliders = false;
                return;
            }
            
            // Count valid objects from both arrays
            int validCount = 0;
            
            // Count regular objects
            if (hasRegularObjects) {
                for (int i = 0; i < objects.Length; i++) {
                    if (objects[i] != null) validCount++;
                }
            }
            
            // Count objects without children
            if (hasNoChildrenObjects) {
                for (int i = 0; i < objectsNoChildren.Length; i++) {
                    if (objectsNoChildren[i] != null) validCount++;
                }
            }
            
            if (validCount == 0) {
                enableScaledColliders = false;
                return;
            }
            
            // Create a container for all virtual objects - using Instantiate instead of new GameObject()
            virtualObjectsContainer = Instantiate(gameObject);
            // Remove all components except Transform
            Component[] containerComponents = virtualObjectsContainer.GetComponents<Component>();
            for (int i = 0; i < containerComponents.Length; i++) {
                string componentType = containerComponents[i].GetType().ToString();
                if (componentType != "UnityEngine.Transform") {
                    Destroy(containerComponents[i]);
                }
            }
            virtualObjectsContainer.name = "ScaledColliders_VirtualObjects";
            
            // Create arrays of the right size
            taggedObjects = new GameObject[validCount];
            virtualObjects = new GameObject[validCount];
            
            // Fill arrays and create virtual copies
            int index = 0;
            
            // Add regular objects (with children)
            if (hasRegularObjects) {
                for (int i = 0; i < objects.Length; i++) {
                    if (objects[i] != null) {
                        taggedObjects[index] = objects[i];
                        PrepareWorldParent(taggedObjects[index], false, true); // Process with children
                        virtualObjects[index] = Instantiate(taggedObjects[index]);
                        
                        // Parent to the container
                        virtualObjects[index].transform.SetParent(virtualObjectsContainer.transform, true);
                        
                        // Explicitly set the world rotation to match the original
                        virtualObjects[index].transform.rotation = taggedObjects[index].transform.rotation;
                        
                        PrepareVirtualWorld(virtualObjects[index], true); // Process with children
                        index++;
                    }
                }
            }
            
            // Add objects without children
            if (hasNoChildrenObjects) {
                for (int i = 0; i < objectsNoChildren.Length; i++) {
                    if (objectsNoChildren[i] != null) {
                        taggedObjects[index] = objectsNoChildren[i];
                        PrepareWorldParent(taggedObjects[index], false, false); // Process without children
                        virtualObjects[index] = Instantiate(taggedObjects[index]);
                        
                        // Parent to the container
                        virtualObjects[index].transform.SetParent(virtualObjectsContainer.transform, true);
                        
                        // Explicitly set the world rotation to match the original
                        virtualObjects[index].transform.rotation = taggedObjects[index].transform.rotation;
                        
                        PrepareVirtualWorld(virtualObjects[index], false); // Process without children
                        index++;
                    }
                }
            }
            
            AdjustLocalScale();
        }

        private void PrepareWorldParent(GameObject obj, bool reset = false, bool includeChildren = true) {
            // Clear existing state tracking arrays if starting fresh
            if (!reset) {
                stateTrackedObjects = new GameObject[0];
                stateWasEnabled = new bool[0];
            }
            
            Collider[] colliders;
            
            if (includeChildren) {
                colliders = obj.GetComponentsInChildren<Collider>(true);
            } else {
                colliders = obj.GetComponents<Collider>();
            }
            
            for (int i = 0; i < colliders.Length; i++) {
                if (colliders[i] == null || colliders[i].isTrigger || colliders[i].GetType().ToString() == "UnityEngine.TerrainCollider") {
                    continue;
                }
                
                // Check if this object should keep its colliders fully functional
                bool keepCollider = ShouldKeepColliders(colliders[i].gameObject);
                
                // Store original enabled state before processing - we'll use this when initializing virtual colliders
                if (!reset) {
                    // Store the original states in our arrays
                    GameObject currentObject = colliders[i].gameObject;
                    bool currentEnabled = colliders[i].enabled;
                    
                    // Expand arrays to fit new entry
                    int oldLength = stateTrackedObjects.Length;
                    GameObject[] newTrackedObjects = new GameObject[oldLength + 1];
                    bool[] newWasEnabled = new bool[oldLength + 1];
                    
                    // Copy existing values
                    for (int j = 0; j < oldLength; j++) {
                        newTrackedObjects[j] = stateTrackedObjects[j];
                        newWasEnabled[j] = stateWasEnabled[j];
                    }
                    
                    // Add new entry
                    newTrackedObjects[oldLength] = currentObject;
                    newWasEnabled[oldLength] = currentEnabled;
                    
                    // Update our arrays
                    stateTrackedObjects = newTrackedObjects;
                    stateWasEnabled = newWasEnabled;
                }
                
                // Process naming convention markers for layer exclusion
                if (colliders[i].name.EndsWith("|-|")) {
                    colliders[i].excludeLayers = 0;
                    // If we're resetting or keeping colliders, re-enable them
                    if (reset || keepCollider) {
                        colliders[i].enabled = true;
                    }
                    colliders[i].name = colliders[i].name.Substring(0, colliders[i].name.Length - 3);
                } else if (colliders[i].name.EndsWith("|~|")) {
                    colliders[i].excludeLayers = colliders[i].excludeLayers & ~collisionMask;
                    // If we're resetting or keeping colliders, re-enable them
                    if (reset || keepCollider) {
                        colliders[i].enabled = true;
                    }
                    colliders[i].name = colliders[i].name.Substring(0, colliders[i].name.Length - 3);
                } else if (colliders[i].name.EndsWith("|=|")) {
                    // If we're resetting or keeping colliders, re-enable them
                    if (reset || keepCollider) {
                        colliders[i].enabled = true;
                    }
                    colliders[i].name = colliders[i].name.Substring(0, colliders[i].name.Length - 3);
                }
                
                if (!reset) {
                    bool hadExcludedLayers = colliders[i].excludeLayers > 0;
                    bool hadExcludedThisLayer = (colliders[i].excludeLayers & collisionMask) == collisionMask;
                    
                    // Only apply layer exclusion if not in the keep list
                    if (!keepCollider) {
                        colliders[i].excludeLayers = colliders[i].excludeLayers | collisionMask;
                        
                        // Add marker to know how to restore it later
                        colliders[i].name += hadExcludedLayers ? hadExcludedThisLayer ? "|=|" : "|~|" : "|-|";
                        
                        // Disable the collider by default unless it's in the keepColliders list
                        colliders[i].enabled = false;
                    }
                }
            }
        }
        
        // Check if this object or any of its parents is in the objectsKeepColliders array
        private bool ShouldKeepColliders(GameObject obj) {
            if (objectsKeepColliders == null || objectsKeepColliders.Length == 0) {
                return false;
            }
            
            GameObject current = obj;
            while (current != null) {
                for (int i = 0; i < objectsKeepColliders.Length; i++) {
                    if (objectsKeepColliders[i] == current) {
                        return true;
                    }
                }
                current = current.transform.parent != null ? current.transform.parent.gameObject : null;
            }
            return false;
        }

        private void PrepareVirtualWorld(GameObject obj, bool includeChildren = true) {
            Component[] components;
            
            if (includeChildren) {
                components = obj.GetComponentsInChildren<Component>(true);
            } else {
                components = obj.GetComponents<Component>();
            }
            
            // First pass - mark components that need to be preserved
            bool[] keepComponent = new bool[components.Length];
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null) continue;
                
                string componentType = components[i].GetType().ToString();
                
                // Always keep Transform components - required for positioning and hierarchy
                if (componentType == "UnityEngine.Transform") {
                    keepComponent[i] = true;
                    continue;
                }
                
                // Keep ALL collider components (except triggers and terrain)
                // These are CRITICAL for the scaled collider system to function properly
                if (componentType.StartsWith("UnityEngine.") && componentType.EndsWith("Collider") &&
                    componentType != "UnityEngine.TerrainCollider" && !((Collider)components[i]).isTrigger) {
                    keepComponent[i] = true;
                    continue;
                }
                
                // Keep required physics components that the colliders might need
                if (componentType == "UnityEngine.Rigidbody") {
                    // Keep Rigidbody components attached to objects with colliders
                    // This ensures physics interactions work correctly with scaled colliders
                    if (components[i].gameObject.GetComponent<Collider>() != null) {
                        keepComponent[i] = true;
                        continue;
                    }
                }
                
                // Only keep these if debug visualization is enabled
                if (showColliderGhosts && ghostMaterial != null) {
                    // MeshFilter needed for showing collider ghosts
                    if (componentType == "UnityEngine.MeshFilter") {
                        keepComponent[i] = true;
                        continue;
                    }
                    
                    // MeshRenderer needed for showing collider ghosts
                    if (componentType == "UnityEngine.MeshRenderer") {
                        keepComponent[i] = true;
                        continue;
                    }
                }
            }
            
            // Sort components by removal order to avoid dependency errors
            // The strategy is to organize removal in multiple phases
            
            // First phase: Remove scripts and non-essential components
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null || keepComponent[i]) continue;
                
                string componentType = components[i].GetType().ToString();
                
                // Skip transforms and essential components - handle these later
                if (componentType == "UnityEngine.Transform" || 
                    componentType.EndsWith("Collider") || 
                    componentType == "UnityEngine.Rigidbody" ||
                    componentType == "UnityEngine.MeshFilter" || 
                    componentType == "UnityEngine.MeshRenderer") {
                    continue;
                }
                
                // Remove all scripts and other non-essential components first
                Destroy(components[i]);
            }
            
            // Second phase: Remove renderers (after scripts that might depend on them)
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null || keepComponent[i]) continue;
                
                string componentType = components[i].GetType().ToString();
                
                if (componentType == "UnityEngine.MeshRenderer" || componentType.EndsWith("Renderer")) {
                    Destroy(components[i]);
                }
            }
            
            // Third phase: Remove mesh filters and other similar components
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null || keepComponent[i]) continue;
                
                string componentType = components[i].GetType().ToString();
                
                if (componentType == "UnityEngine.MeshFilter") {
                    Destroy(components[i]);
                }
            }
            
            // Fourth phase: Remove physics components that aren't needed for collisions
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null || keepComponent[i]) continue;
                
                string componentType = components[i].GetType().ToString();
                
                // DO NOT remove any colliders that weren't already marked for removal in previous passes
                // We only want to remove non-essential physics components here
                if (componentType == "UnityEngine.Rigidbody") {
                    // Double-check that this Rigidbody isn't needed for a collider
                    if (components[i].gameObject.GetComponent<Collider>() == null) {
                        Destroy(components[i]);
                    }
                }
            }
            
            // Configure components we're keeping
            for (int i = 0; i < components.Length; i++) {
                if (components[i] == null || components[i].gameObject == null || !keepComponent[i]) continue;
                
                string componentType = components[i].GetType().ToString();
                
                // Configure colliders - THIS IS THE CRITICAL PART FOR SCALED COLLIDER FUNCTIONALITY
                if (componentType.StartsWith("UnityEngine.") && componentType.EndsWith("Collider")) {
                    Collider collider = (Collider)components[i];
                    
                    // Set up base configuration
                    collider.excludeLayers = ~collisionMask;
                    collider.includeLayers = collisionMask;
                    collider.enabled = true;
                    
                    // Check if we have stored state to preserve from original
                    GameObject originalObj = GetOriginalObjectFromVirtual(obj);
                    if (originalObj != null) {
                        // Try to find matching collider in original to copy state
                        string path = GetTransformPath(collider.transform, obj.transform);
                        bool originalWasEnabled = GetOriginalEnabledState(originalObj, path, collider.GetType().ToString());
                        
                        // Apply the original enabled state
                        collider.enabled = originalWasEnabled;
                    }
                }
                // Configure Rigidbody components we're keeping
                else if (componentType == "UnityEngine.Rigidbody") {
                    ((Rigidbody)components[i]).isKinematic = true;
                    ((Rigidbody)components[i]).useGravity = false;
                }
                // Configure MeshRenderer for debug visualization
                else if (componentType == "UnityEngine.MeshRenderer" && showColliderGhosts && ghostMaterial != null) {
                    ((MeshRenderer)components[i]).shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    ((MeshRenderer)components[i]).receiveShadows = false;
                    int materialCount = ((MeshRenderer)components[i]).materials.Length;
                    Material[] newMaterials = new Material[materialCount];
                    for (int j = 0; j < materialCount; j++) {
                        newMaterials[j] = ghostMaterial;
                    }
                    ((MeshRenderer)components[i]).materials = newMaterials;
                }
            }
            
            // Special handling for no-children case
            if (!includeChildren) {
                // Get all child transforms
                Transform[] childTransforms = obj.GetComponentsInChildren<Transform>(true);
                
                // Create a list to store children that need to be destroyed
                GameObject[] childrenToDestroy = new GameObject[childTransforms.Length];
                int count = 0;
                
                for (int i = 0; i < childTransforms.Length; i++) {
                    // Skip the object's own transform
                    if (childTransforms[i] == obj.transform) continue;
                    
                    // Add this child to our destroy list
                    childrenToDestroy[count++] = childTransforms[i].gameObject;
                }
                
                // Destroy all children after enumeration
                for (int i = 0; i < count; i++) {
                    Destroy(childrenToDestroy[i]);
                }
            }
        }
        
        private bool HasComponent(GameObject obj, string typeName) {
            Component[] components = obj.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++) {
                if (components[i] != null && components[i].GetType().ToString() == typeName) {
                    return true;
                }
            }
            return false;
        }
        
        // New method to sync basic object states (active/inactive) between original and virtual copy
        private void SyncObjectStates(GameObject original, GameObject virtualCopy) {
            if (original == null || virtualCopy == null) return;
            
            // Sync top-level object active state
            virtualCopy.SetActive(original.activeSelf);
            
            // First get all child transforms from both objects
            Transform[] originalTransforms = original.GetComponentsInChildren<Transform>(true);
            Transform[] virtualTransforms = virtualCopy.GetComponentsInChildren<Transform>(true);
            
            // For each original child transform
            for (int i = 0; i < originalTransforms.Length; i++) {
                if (originalTransforms[i] == null) continue;
                
                // Get path relative to the root object
                string originalPath = GetTransformPath(originalTransforms[i], original.transform);
                
                // Find the matching transform in the virtual copy
                for (int j = 0; j < virtualTransforms.Length; j++) {
                    if (virtualTransforms[j] == null) continue;
                    
                    string virtualPath = GetTransformPath(virtualTransforms[j].transform, virtualCopy.transform);
                    if (originalPath == virtualPath) {
                        // Found the matching transform, sync the active state
                        GameObject originalChild = originalTransforms[i].gameObject;
                        GameObject virtualChild = virtualTransforms[j].gameObject;
                        
                        // Only sync active state if the objects are different
                        if (virtualChild.activeSelf != originalChild.activeSelf) {
                            virtualChild.SetActive(originalChild.activeSelf);
                        }
                        
                        break;
                    }
                }
            }
        }
        
        // New method to sync collider states between original object and virtual copy
        private void SyncColliderStates(GameObject original, GameObject virtualCopy) {
            if (original == null || virtualCopy == null) return;
            
            // Dictionary isn't supported in UdonSharp, so we'll use arrays instead
            // Get all non-trigger colliders in both objects
            Collider[] vColliders = virtualCopy.GetComponentsInChildren<Collider>(true);
            Collider[] originalColliders = original.GetComponentsInChildren<Collider>(true);
            
            // For each original collider that should be kept
            for (int i = 0; i < originalColliders.Length; i++) {
                if (originalColliders[i].isTrigger || 
                    originalColliders[i].GetType().ToString() == "UnityEngine.TerrainCollider" ||
                    !ShouldKeepColliders(originalColliders[i].gameObject)) {
                    continue;
                }
                
                // Find the matching collider in the virtual object
                Transform originalTransform = originalColliders[i].transform;
                string originalPath = GetTransformPath(originalTransform, original.transform);
                
                // Look for a matching collider in the virtual copy
                for (int j = 0; j < vColliders.Length; j++) {
                    if (vColliders[j].isTrigger || 
                        vColliders[j].GetType().ToString() == "UnityEngine.TerrainCollider") {
                        continue;
                    }
                    
                    // Check if this is the matching collider
                    string virtualPath = GetTransformPath(vColliders[j].transform, virtualCopy.transform);
                    if (originalPath == virtualPath && originalColliders[i].GetType() == vColliders[j].GetType()) {
                        // Found the matching collider, sync the enabled state
                        vColliders[j].enabled = originalColliders[i].enabled;
                        break;
                    }
                }
            }
        }
        
        // Helper method to get the hierarchical path of a transform
        private string GetTransformPath(Transform objectTransform, Transform rootTransform) {
            if (objectTransform == rootTransform) return objectTransform.name;
            
            string path = objectTransform.name;
            Transform parent = objectTransform.parent;
            
            // Build path from leaf to root
            while (parent != null && parent != rootTransform) {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            
            // Handle case where there are multiple colliders on the same object
            Collider[] colliders = objectTransform.GetComponents<Collider>();
            if (colliders.Length > 1) {
                // Add the component's index to the path
                for (int i = 0; i < colliders.Length; i++) {
                    if (colliders[i].gameObject == objectTransform.gameObject) {
                        path += "_" + i.ToString();
                        break;
                    }
                }
            }
            
            return path;
        }
        
        // Helper method to find original object from a virtual copy
        private GameObject GetOriginalObjectFromVirtual(GameObject virtualObj) {
            if (selectionMode == ScaleSelectionMode.ParentObject) {
                if (virtualWorld == virtualObj) {
                    return worldParent;
                }
            } else {
                // Search through virtual object mappings
                for (int i = 0; i < virtualObjects.Length; i++) {
                    if (virtualObjects[i] == virtualObj && i < taggedObjects.Length) {
                        return taggedObjects[i];
                    }
                }
            }
            return null;
        }
        
        // Helper method to find matching collider in original object
        private Collider FindMatchingCollider(GameObject originalObj, string path, string colliderType) {
            // Get all colliders in original
            Collider[] originalColliders;
            if (path.Contains("/")) {
                originalColliders = originalObj.GetComponentsInChildren<Collider>(true);
            } else {
                originalColliders = originalObj.GetComponents<Collider>();
            }
            
            // Find matching collider by path and type
            for (int i = 0; i < originalColliders.Length; i++) {
                if (originalColliders[i].GetType().ToString() == colliderType) {
                    string originalPath = GetTransformPath(originalColliders[i].transform, originalObj.transform);
                    if (originalPath == path) {
                        return originalColliders[i];
                    }
                }
            }
            
            return null;
        }

        // New helper method to get the stored enabled state
        private bool GetOriginalEnabledState(GameObject originalObj, string path, string colliderType) {
            // Look through our stored state arrays
            for (int i = 0; i < stateTrackedObjects.Length; i++) {
                if (stateTrackedObjects[i] == null) continue;
                
                // Find the object with the matching collider
                Collider[] colliders = stateTrackedObjects[i].GetComponents<Collider>();
                for (int j = 0; j < colliders.Length; j++) {
                    if (colliders[j] != null && colliders[j].GetType().ToString() == colliderType) {
                        string colliderPath = GetTransformPath(colliders[j].transform, originalObj.transform);
                        if (path == colliderPath && i < stateWasEnabled.Length) {
                            return stateWasEnabled[i];
                        }
                    }
                }
            }
            
            return true; // Default to enabled if we can't find the state
        }
    }
}
