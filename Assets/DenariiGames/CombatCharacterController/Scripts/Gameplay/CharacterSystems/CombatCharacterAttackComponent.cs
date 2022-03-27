/**
 * CombatCharacterAttackComponent
 * Author: Denarii Games
 * Version: 1.0
 *
 * Replace DefaultCharacterAttackComponent on PlayerCharacter prefab
 */

using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLibManager;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace MultiplayerARPG
{
	public class CombatCharacterAttackComponent : BaseNetworkedGameEntityComponent<BaseCharacterEntity>, ICharacterAttackComponent
	{
		protected List<CancellationTokenSource> attackCancellationTokenSources = new List<CancellationTokenSource>();
		public bool IsAttacking { get; protected set; }
		public float LastAttackEndTime { get; protected set; }
		public float MoveSpeedRateWhileAttacking { get; protected set; }
		public AnimActionType AnimActionType { get; protected set; }
		public int AnimActionDataId { get; protected set; }

		protected readonly Dictionary<int, SimulatingHit> SimulatingHits = new Dictionary<int, SimulatingHit>();

		//combat uses rigidbodymovement during animation
		RigidBodyEntityMovement rigidBodyEntityMovement;
		bool rbem_useRootMotionForMovement = false;
		bool rbem_useRootMotionForJump = false;

		void Start()
		{
			if (GameInstance.Singleton.enableRigidBodyCombatAttack)
			{
				rigidBodyEntityMovement = GetComponent<RigidBodyEntityMovement>();
				if (rigidBodyEntityMovement != null)
				{
					rbem_useRootMotionForMovement = rigidBodyEntityMovement.useRootMotionForMovement;
					rbem_useRootMotionForJump = rigidBodyEntityMovement.useRootMotionForJump;
				}
			}

		}

		protected virtual void SetAttackActionStates(AnimActionType animActionType, int animActionDataId)
		{
			ClearAttackStates();
			AnimActionType = animActionType;
			AnimActionDataId = animActionDataId;
			IsAttacking = true;
		}

		public virtual void ClearAttackStates()
		{
			IsAttacking = false;
		}

		public bool CallServerAttack(byte simulateSeed, bool isLeftHand, int combatAnim)
		{
			RPC(ServerAttack, BaseCharacterEntity.ACTION_TO_CLIENT_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, simulateSeed, isLeftHand, combatAnim);
			return true;
		}

		/// <summary>
		/// Is function will be called at server to order character to use skill
		/// </summary>
		/// <param name="simulateSeed"></param>
		/// <param name="isLeftHand"></param>
		/// <param name="combatAnim"></param>
		[ServerRpc]
		protected void ServerAttack(byte simulateSeed, bool isLeftHand, int combatAnim)
		{
#if !CLIENT_BUILD
			// Speed hack avoidance
			if (Time.unscaledTime - LastAttackEndTime < -0.05f)
			{
				return;
			}

			// Set attack state
			IsAttacking = true;

			// Play animations
			CallAllPlayAttackAnimation(simulateSeed, isLeftHand, combatAnim);
#endif
		}

		public bool CallAllPlayAttackAnimation(byte simulateSeed, bool isLeftHand, int combatAnim)
		{
			RPC(AllPlayAttackAnimation, BaseCharacterEntity.ACTION_TO_CLIENT_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, simulateSeed, isLeftHand, combatAnim);
			return true;
		}

		[AllRpc]
		protected void AllPlayAttackAnimation(byte simulateSeed, bool isLeftHand, int combatAnim)
		{
			if (IsOwnerClientOrOwnedByServer)
				return;
			AttackRoutine(simulateSeed, isLeftHand, combatAnim).Forget();
		}

		protected async UniTaskVoid AttackRoutine(byte simulateSeed, bool isLeftHand, int combatAnim)
		{
			// Prepare cancellation
			CancellationTokenSource attackCancellationTokenSource = new CancellationTokenSource();
			attackCancellationTokenSources.Add(attackCancellationTokenSource);

			// Prepare required data and get weapon data
			AnimActionType animActionType;
			int animActionDataId;
			CharacterItem weapon;
			Entity.GetAttackingData(
				ref isLeftHand,
				out animActionType,
				out animActionDataId,
				out weapon);

			// Prepare required data and get animation data
			int animationIndex;
			float animSpeedRate;
			float[] triggerDurations;
			float totalDuration;
			Entity.GetRandomAnimationData(
				animActionType,
				animActionDataId,
				simulateSeed,
				out animationIndex,
				out animSpeedRate,
				out triggerDurations,
				out totalDuration);

			// combatAnim override random index
			animationIndex = combatAnim;

			// Set doing action state at clients and server
			SetAttackActionStates(animActionType, animActionDataId);

			// Prepare required data and get damages data
			IWeaponItem weaponItem = weapon.GetWeaponItem();
			DamageInfo damageInfo = Entity.GetWeaponDamageInfo(weaponItem);
			Dictionary<DamageElement, MinMaxFloat> damageAmounts = Entity.GetWeaponDamagesWithBuffs(weapon);

			// Calculate move speed rate while doing action at clients and server
			MoveSpeedRateWhileAttacking = Entity.GetMoveSpeedRateWhileAttacking(weaponItem);

			// Get play speed multiplier will use it to play animation faster or slower based on attack speed stats
			animSpeedRate *= Entity.GetAnimSpeedRate(AnimActionType);

			// Last attack end time
			LastAttackEndTime = Time.unscaledTime + (totalDuration / animSpeedRate);

			//combat set rigidbody movement on
			if (rigidBodyEntityMovement != null)
			{
				rigidBodyEntityMovement.useRootMotionForMovement = true;
				rigidBodyEntityMovement.useRootMotionForJump = true;
			}

			try
			{
				// Play action animation
				if (Entity.CharacterModel && Entity.CharacterModel.gameObject.activeSelf)
				{
					// TPS model
					Entity.CharacterModel.PlayActionAnimation(AnimActionType, AnimActionDataId, animationIndex, animSpeedRate);
				}
				if (Entity.PassengingVehicleEntity != null && Entity.PassengingVehicleEntity.Entity.Model &&
					Entity.PassengingVehicleEntity.Entity.Model.gameObject.activeSelf &&
					Entity.PassengingVehicleEntity.Entity.Model is BaseCharacterModel)
				{
					// Vehicle model
					(Entity.PassengingVehicleEntity.Entity.Model as BaseCharacterModel).PlayActionAnimation(AnimActionType, AnimActionDataId, animationIndex, animSpeedRate);
				}
				if (IsClient)
				{
					if (Entity.FpsModel && Entity.FpsModel.gameObject.activeSelf)
					{
						// FPS model
						Entity.FpsModel.PlayActionAnimation(AnimActionType, AnimActionDataId, animationIndex, animSpeedRate);
					}
				}

				float remainsDuration = totalDuration;
				float tempTriggerDuration;
				SimulatingHits[simulateSeed] = new SimulatingHit(triggerDurations.Length);
				for (int hitIndex = 0; hitIndex < triggerDurations.Length; ++hitIndex)
				{
					// Play special effects after trigger duration
					tempTriggerDuration = triggerDurations[hitIndex];
					remainsDuration -= tempTriggerDuration;
					await UniTask.Delay((int)(tempTriggerDuration / animSpeedRate * 1000f), true, PlayerLoopTiming.Update, attackCancellationTokenSource.Token);

					// Special effects will plays on clients only
					if (IsClient)
					{
						// Play weapon launch special effects
						if (Entity.CharacterModel && Entity.CharacterModel.gameObject.activeSelf)
							Entity.CharacterModel.PlayEquippedWeaponLaunch(isLeftHand);
						if (Entity.FpsModel && Entity.FpsModel.gameObject.activeSelf)
							Entity.FpsModel.PlayEquippedWeaponLaunch(isLeftHand);
						// Play launch sfx
						if (AnimActionType == AnimActionType.AttackRightHand ||
							AnimActionType == AnimActionType.AttackLeftHand)
						{
							AudioManager.PlaySfxClipAtAudioSource(weaponItem.LaunchClip, Entity.CharacterModel.GenericAudioSource);
						}
					}

					// Call on attack to extend attack functionality while attacking
					bool overrideDefaultAttack = false;
					foreach (KeyValuePair<BaseSkill, short> skillLevel in Entity.GetCaches().Skills)
					{
						if (skillLevel.Value <= 0)
							continue;
						if (skillLevel.Key.OnAttack(Entity, skillLevel.Value, isLeftHand, weapon, hitIndex, damageAmounts, Entity.AimPosition))
							overrideDefaultAttack = true;
					}

					// Skip attack function when applied skills (buffs) will override default attack functionality
					if (!overrideDefaultAttack)
					{
						// Trigger attack event
						Entity.OnAttackRoutine(isLeftHand, weapon, hitIndex, damageInfo, damageAmounts, Entity.AimPosition);

						// Apply attack damages
						if (IsOwnerClientOrOwnedByServer)
						{
							long time = BaseGameNetworkManager.Singleton.ServerTimestamp;
							int attackSeed = unchecked(simulateSeed + (hitIndex * 16));
							ApplyAttack(isLeftHand, weapon, damageInfo, damageAmounts, Entity.AimPosition, attackSeed, time);
							SimulateLaunchDamageEntityData simulateData = new SimulateLaunchDamageEntityData();
							if (isLeftHand)
								simulateData.state |= SimulateLaunchDamageEntityState.IsLeftHand;
							simulateData.randomSeed = simulateSeed;
							simulateData.aimPosition = Entity.AimPosition;
							simulateData.time = time;
							CallAllSimulateLaunchDamageEntity(simulateData);
						}
					}

					if (remainsDuration <= 0f)
					{
						// Stop trigger animations loop
						break;
					}
				}

				if (IsServer && weaponItem.DestroyImmediatelyAfterFired)
				{
					EquipWeapons equipWeapons = Entity.EquipWeapons;
					if (isLeftHand)
						equipWeapons.leftHand = CharacterItem.Empty;
					else
						equipWeapons.rightHand = CharacterItem.Empty;
					Entity.EquipWeapons = equipWeapons;
				}

				if (remainsDuration > 0f)
				{
					// Wait until animation ends to stop actions
					await UniTask.Delay((int)(remainsDuration / animSpeedRate * 1000f), true, PlayerLoopTiming.Update, attackCancellationTokenSource.Token);
				}
			}
			catch (System.OperationCanceledException)
			{
				// Catch the cancellation
				LastAttackEndTime = Time.unscaledTime;
			}
			catch (System.Exception ex)
			{
				// Other errors
				Logging.LogException(LogTag, ex);
			}
			finally
			{
				attackCancellationTokenSource.Dispose();
				attackCancellationTokenSources.Remove(attackCancellationTokenSource);

				//combat set rigidbody movement off
				if (rigidBodyEntityMovement != null)
				{
					rigidBodyEntityMovement.useRootMotionForMovement = rbem_useRootMotionForMovement;
					rigidBodyEntityMovement.useRootMotionForJump = rbem_useRootMotionForJump;
				}

			}
			// Clear action states at clients and server
			ClearAttackStates();
		}

		protected virtual void ApplyAttack(bool isLeftHand, CharacterItem weapon, DamageInfo damageInfo, Dictionary<DamageElement, MinMaxFloat> damageAmounts, AimPosition aimPosition, int randomSeed, long? time)
		{
			if (IsServer)
			{
				// Increase damage with ammo damage
				Dictionary<DamageElement, MinMaxFloat> increaseDamages;
				Entity.DecreaseAmmos(weapon, isLeftHand, 1, out increaseDamages);
				if (increaseDamages != null)
					damageAmounts = GameDataHelpers.CombineDamages(damageAmounts, increaseDamages);
			}

			byte fireSpread = 0;
			Vector3 fireStagger = Vector3.zero;
			if (weapon != null && weapon.GetWeaponItem() != null)
			{
				// For monsters, their weapon can be null so have to avoid null exception
				fireSpread = weapon.GetWeaponItem().FireSpread;
				fireStagger = weapon.GetWeaponItem().FireStagger;
			}

			// Fire
			System.Random random = new System.Random(randomSeed);
			Vector3 stagger;
			for (int i = 0; i < fireSpread + 1; ++i)
			{
				stagger = new Vector3();
				stagger.x = GenericUtils.RandomFloat(random.Next(), -fireStagger.x, fireStagger.x);
				stagger.y = GenericUtils.RandomFloat(random.Next(), -fireStagger.y, fireStagger.y);
				damageInfo.LaunchDamageEntity(
					Entity,
					isLeftHand,
					weapon,
					damageAmounts,
					null,
					0,
					randomSeed,
					aimPosition,
					stagger,
					out _,
					time);
			}
		}

		public bool CallAllSimulateLaunchDamageEntity(SimulateLaunchDamageEntityData data)
		{
			RPC(AllSimulateLaunchDamageEntity, BaseCharacterEntity.ACTION_TO_CLIENT_DATA_CHANNEL, DeliveryMethod.ReliableOrdered, data);
			return true;
		}

		[AllRpc]
		protected void AllSimulateLaunchDamageEntity(SimulateLaunchDamageEntityData data)
		{
			if (IsOwnerClientOrOwnedByServer)
				return;
			SimulatingHit simulatingHit;
			if (!SimulatingHits.TryGetValue(data.randomSeed, out simulatingHit) || simulatingHit.hitIndex >= simulatingHit.triggerLength)
				return;
			int hitIndex = SimulatingHits[data.randomSeed].hitIndex + 1;
			simulatingHit.hitIndex = hitIndex;
			SimulatingHits[data.randomSeed] = simulatingHit;
			bool isLeftHand = data.state.HasFlag(SimulateLaunchDamageEntityState.IsLeftHand);
			if (!data.state.HasFlag(SimulateLaunchDamageEntityState.IsSkill))
			{
				CharacterItem weapon = Entity.GetAvailableWeapon(ref isLeftHand);
				DamageInfo damageInfo = Entity.GetWeaponDamageInfo(weapon.GetWeaponItem());
				Dictionary<DamageElement, MinMaxFloat> damageAmounts = Entity.GetWeaponDamagesWithBuffs(weapon);
				int attackSeed = unchecked(data.randomSeed + (hitIndex * 16));
				ApplyAttack(isLeftHand, weapon, damageInfo, damageAmounts, data.aimPosition, attackSeed, data.time);
			}
		}

		public void CancelAttack()
		{
			for (int i = attackCancellationTokenSources.Count - 1; i >= 0; --i)
			{
				if (!attackCancellationTokenSources[i].IsCancellationRequested)
					attackCancellationTokenSources[i].Cancel();
				attackCancellationTokenSources.RemoveAt(i);
			}
		}

		public void Attack(bool isLeftHand)
		{
			// Set attack state
			IsAttacking = true;

			// Get simulate seed for simulation validating
			byte simulateSeed = (byte)Random.Range(byte.MinValue, byte.MaxValue);

			// Simulate attacking at client immediately
			AttackRoutine(simulateSeed, isLeftHand, Entity.CombatAnim).Forget();

			// Tell the server to attack
			if (!IsServer)
				CallServerAttack(simulateSeed, isLeftHand, Entity.CombatAnim);
			else if (IsOwnerClientOrOwnedByServer)
				CallAllPlayAttackAnimation(simulateSeed, isLeftHand, Entity.CombatAnim);
		}
	}
}
