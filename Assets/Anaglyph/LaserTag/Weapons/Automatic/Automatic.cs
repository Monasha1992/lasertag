using System;
using Anaglyph.Lasertag.Logistics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace Anaglyph.Lasertag.Weapons
{
	public class Automatic : MonoBehaviour
	{
		[SerializeField] private int fixedUpdatesPerFire = 5;
		private int fixedUpdateTilNextFire = 0;

		[SerializeField] private GameObject boltPrefab = null;
		[SerializeField] private Transform emitFromTransform = null;
		public UnityEvent onFire = new();

		// Raised on each LOCAL auto-fire tick. See Blaster.LocalFired — same hook
		// for GameEventCollector's `shot_fired` metric (full-auto raises it once
		// per bolt, which is the intended per-shot granularity).
		public static event Action LocalFired = delegate { };

		private bool firing;

		public void OnFire(InputAction.CallbackContext context)
		{
			firing = context.ReadValueAsButton();
		}

		private void FixedUpdate()
		{
			if (!NetworkManager.Singleton.IsConnectedClient || !WeaponsManagement.CanFire)
				return;

			if (firing)
			{
				fixedUpdateTilNextFire -= 1;

				if(fixedUpdateTilNextFire <= 0)
				{
					Fire();
					fixedUpdateTilNextFire = fixedUpdatesPerFire;
				}
			} else {
				fixedUpdateTilNextFire = 0;
			}

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
