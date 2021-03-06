﻿using System.Threading.Tasks;
using Duality;
using Jazz2.Actors.Enemies;
using Jazz2.Actors.Weapons;
using Jazz2.Game.Structs;

namespace Jazz2.Actors
{
    public class Bird : ActorBase
    {
        private ushort type;
        private Player owner;
        private float fireCooldown = 80f;
        private bool flyAway;
        private float attackTime;

        protected override async Task OnActivatedAsync(ActorActivationDetails details)
        {
            type = details.Params[0];
            switch (type) {
                case 0: // Chuck (red)
                    await RequestMetadataAsync("Object/BirdChuck");
                    break;
                case 1: // Birdy (yellow)
                    await RequestMetadataAsync("Object/BirdBirdy");
                    break;
            }

            SetAnimation(AnimState.Idle);

            CollisionFlags = CollisionFlags.None;
        }

        public void OnLinkWithPlayer(Player owner)
        {
            this.owner = owner;
        }

        public override void OnFixedUpdate(float timeMult)
        {
            //base.OnFixedUpdate(timeMult);

            if (owner == null) {
                return;
            }

            Vector3 currentPos = Transform.Pos;

            if (flyAway) {
                // Fly away
                if (fireCooldown <= 0f) {
                    DecreaseHealth(int.MaxValue);
                } else {
                    fireCooldown -= timeMult;

                    currentPos.X += (IsFacingLeft ? -8f : 8f) * timeMult;
                    currentPos.Y -= 1f * timeMult;
                    Transform.Pos = currentPos;
                }
                return;
            }
            
            if (type == 1 && attackTime > 0f) {
                // Attack
                currentPos.X += speedX * timeMult;
                currentPos.Y += speedY * timeMult;
                Transform.Pos = currentPos;

                attackTime -= timeMult;

                if (attackTime <= 0f) {
                    SetAnimation(AnimState.Idle);
                    CollisionFlags = CollisionFlags.None;
                    Transform.Angle = 0f;
                }
            } else {
                // Follow player
                Vector3 targetPos = owner.Transform.Pos;
                bool playerFacingLeft = owner.IsFacingLeft;

                if (playerFacingLeft) {
                    targetPos.X += 55f;
                } else {
                    targetPos.X -= 55f;
                }
                targetPos.Y -= 50f;
                targetPos.Z = currentPos.Z;

                if (IsFacingLeft != playerFacingLeft) {
                    IsFacingLeft = playerFacingLeft;
                    fireCooldown = 40f;
                }

                targetPos.X = MathF.Lerp(currentPos.X, targetPos.X, 0.02f * timeMult);
                targetPos.Y = MathF.Lerp(currentPos.Y, targetPos.Y, 0.02f * timeMult);

                Transform.Pos = targetPos;
            }

            // Fire
            if (fireCooldown <= 0f) {
                TryFire();
            } else {
                fireCooldown -= timeMult;
            }
        }

        public override void OnHandleCollision(ActorBase other)
        {
            if (attackTime <= 0f || other.IsInvulnerable) {
                return;
            }

            switch (other) {
                case EnemyBase enemy:
                    enemy.DecreaseHealth(1, this);

                    SetAnimation(AnimState.Idle);
                    attackTime = 0f;
                    CollisionFlags = CollisionFlags.None;
                    Transform.Angle = 0f;
                    break;
            }
        }

        protected override void OnAnimationFinished()
        {
            base.OnAnimationFinished();

            PlaySound("Fly", 0.3f);
        }

        public void FlyAway()
        {
            flyAway = true;
            fireCooldown = 300f;

            if (attackTime > 0f) {
                SetAnimation(AnimState.Idle);
                attackTime = 0f;
                CollisionFlags = CollisionFlags.None;
                Transform.Angle = 0f;
            }
        }

        private void TryFire()
        {
            Vector3 pos = Transform.Pos;

            foreach (GameObject o in levelHandler.ActiveObjects) {
                EnemyBase enemy = o as EnemyBase;
                if (enemy != null && !enemy.IsInvulnerable && enemy.CanCollideWithAmmo) {
                    Vector3 newPos = enemy.Transform.Pos;

                    if ((IsFacingLeft && newPos.X > pos.X) || (!IsFacingLeft && newPos.X < pos.X)) {
                        continue;
                    }

                    float distance = (newPos - pos).Length;
                    if (distance > 260f) {
                        continue;
                    }

                    switch (type) {
                        case 0: { // Chuck (red)
                            pos.Z += 2f;

                            AmmoBlaster newAmmo = new AmmoBlaster();
                            newAmmo.OnActivated(new ActorActivationDetails {
                                LevelHandler = levelHandler,
                                Pos = pos,
                                Params = new ushort[] { 0 }
                            });
                            levelHandler.AddActor(newAmmo);
                            newAmmo.OnFire(owner, pos, Speed, 0f, IsFacingLeft);

                            newAmmo = new AmmoBlaster();
                            newAmmo.OnActivated(new ActorActivationDetails {
                                LevelHandler = levelHandler,
                                Pos = pos,
                                Params = new ushort[] { 0 }
                            });
                            levelHandler.AddActor(newAmmo);
                            newAmmo.OnFire(owner, pos, Speed, IsFacingLeft ? -0.18f : 0.18f, IsFacingLeft);

                            PlaySound("Fire", 0.5f);
                            fireCooldown = 48f;
                            break;
                        }

                        case 1: { // Birdy (yellow)
                            SetAnimation(AnimState.Shoot);

                            Vector3 attackSpeed = (newPos - pos).Normalized;
                            speedX = attackSpeed.X * 6f;
                            speedY = attackSpeed.Y * 6f;
                            Transform.Angle = MathF.Atan2(speedY, speedX);

                            if (IsFacingLeft) {
                                Transform.Angle += MathF.Pi;
                            }

                            attackTime = distance * 0.2f;
                            fireCooldown = 140f;

                            CollisionFlags = CollisionFlags.CollideWithOtherActors;
                            break;
                        }
                    }
                    
                    break;
                }
            }
        }
    }
}