using System;
using Anaglyph.Lasertag.Logistics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Anaglyph.Lasertag.Weapons
{
	public class Blaster : MonoBehaviour
	{
		[SerializeField] private GameObject boltPrefab;
		[SerializeField] private Transform emitFromTransform;
		public UnityEvent onFire = new();

		// Raised when the LOCAL player fires (Fire() runs only from local input).
		// GameEventCollector subscribes to log a `shot_fired` metrics event — the
		// runtime-spawned weapon prefab can't reference the scene logger via the
		// Inspector, so a static event is the clean cross-boundary hook.
		public static event Action LocalFired = delegate { };

		private void OnFire(InputAction.CallbackContext context)
		{
			if(context.performed && context.ReadValueAsButton())
				Fire();
		}

		public void Fire()
		{
			if (!NetworkManager.Singleton.IsConnectedClient || !WeaponsManagement.CanFire)
				return;
			
			// var e = emitFromTransform;
			// NetworkObject.InstantiateAndSpawn(boltPrefab, NetworkManager.Singleton,
			// 	position: e.position, rotation: e.rotation,
			// 	ownerClientId: NetworkManager.Singleton.LocalClientId);

			NetworkObject n = NetworkObjectPool.Instance.GetNetworkObject(
				boltPrefab, emitFromTransform.position, emitFromTransform.rotation);
			
			n.SpawnWithOwnership(NetworkManager.Singleton.LocalClientId);
			
			onFire.Invoke();
			LocalFired.Invoke();
		}
	}
}
