﻿using System.Collections.Generic;
using Duality;
using Jazz2.Game.Structs;

namespace Jazz2.Actors.Enemies
{
    public class Raven : EnemyBase
    {
        private Vector3 originPos, lastPos, targetPos, lastSpeed;
        private float anglePhase;
        private float attackTime = 220f;
        private bool attacking;

        public override void OnAttach(ActorInstantiationDetails details)
        {
            base.OnAttach(details);

            collisionFlags &= ~CollisionFlags.ApplyGravitation;

            SetHealthByDifficulty(2);
            scoreValue = 300;

            originPos = lastPos = targetPos = details.Pos;

            RequestMetadata("Enemy/Raven");
            SetAnimation(AnimState.Idle);

            isFacingLeft = MathF.Rnd.NextBool();
        }

        protected override void OnUpdate()
        {
            HandleBlinking();

            if (frozenTimeLeft > 0) {
                frozenTimeLeft -= Time.TimeMult;
                return;
            }

            if (currentTransitionState == AnimState.Idle) {
                if (attackTime > 0f) {
                    attackTime -= Time.TimeMult;
                } else {
                    if (attacking) {
                        SetAnimation(AnimState.Idle);

                        targetPos = originPos;

                        attackTime = 90f;
                        attacking = false;
                    } else {
                        AttackNearestPlayer();
                    }
                }
            }

            anglePhase += Time.TimeMult * 0.04f;

            if ((targetPos - lastPos).Length > 5f) {
                Vector3 speed = ((targetPos - lastPos).Normalized * (attacking ? 4.6f : 2.6f) + lastSpeed * 1.4f) / 2.4f;
                lastPos.X += speed.X;
                lastPos.Y += speed.Y;
                lastSpeed = speed;

                bool willFaceLeft = (speed.X < 0f);
                if (isFacingLeft != willFaceLeft) {
                    isFacingLeft = willFaceLeft;
                    SetTransition(AnimState.TransitionTurn, false, delegate {
                        RefreshFlipMode();
                    });
                }
            }

            Transform.Pos = lastPos + new Vector3(0f, MathF.Sin(anglePhase) * 6f, 0f);
        }

        protected override bool OnPerish(ActorBase collider)
        {
            CreateDeathDebris(collider);
            api.PlayCommonSound(this, "COMMON_SPLAT");

            TryGenerateRandomDrop();

            return base.OnPerish(collider);
        }

        private void AttackNearestPlayer()
        {
            bool found = false;
            Vector3 foundPos = new Vector3(float.MaxValue, float.MaxValue, lastPos.Z);

            List<Player> players = api.Players;
            for (int i = 0; i < players.Count; i++) {
                Vector3 newPos = players[i].Transform.Pos;
                if ((lastPos - newPos).Length < (lastPos - foundPos).Length) {
                    foundPos = newPos;
                    found = true;
                }
            }

            Vector3 diff = (foundPos - lastPos);
            if (found && diff.Length <= 300f) {
                SetAnimation(AnimState.TransitionAttack);

                targetPos = foundPos;
                targetPos.Y -= 30f;

                attackTime = 80f;
                attacking = true;
            }
        }
    }
}