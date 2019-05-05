﻿using Duality;
using Jazz2.Actors.Weapons;
using Jazz2.Game.Structs;

namespace Jazz2.Actors.Environment
{
    public class BirdCage : ActorBase
    {
        private ushort type;
        private bool activated;

        public override void OnAttach(ActorInstantiationDetails details)
        {
            details.Pos.Z -= 50f;

            base.OnAttach(details);

            type = details.Params[0];
            activated = (details.Params[1] != 0);

            switch (type) {
                case 0: // Chuck (red)
                    RequestMetadata("Object/BirdCageChuck");
                    break;
                case 1: // Birdy (yellow)
                    RequestMetadata("Object/BirdCageBirdy");
                    break;
            }
            
            SetAnimation(activated ? AnimState.Activated : AnimState.Idle);

            collisionFlags |= CollisionFlags.CollideWithSolidObjects;
        }

        public override void OnHandleCollision(ActorBase other)
        {
            switch (other) {
                case Player player: {
                    if (!activated && player.CanBreakSolidObjects) {
                        ApplyToPlayer(player);
                    }
                    break;
                }

                case AmmoBase ammo: {
                    Player player = ammo.Owner;
                    if (!activated && player != null) {
                        ApplyToPlayer(player);

                        ammo.DecreaseHealth(int.MaxValue);
                    }
                    break;
                }
            }
        }

        private void ApplyToPlayer(Player player)
        {
            player.SpawnBird(type, Transform.Pos);

            activated = true;
            SetAnimation(activated ? AnimState.Activated : AnimState.Idle);
            
            Explosion.Create(api, Transform.Pos + new Vector3(-12f, -6f, -20f), Explosion.SmokeBrown);
            Explosion.Create(api, Transform.Pos + new Vector3(-8f, 28f, -20f), Explosion.SmokeBrown);
            Explosion.Create(api, Transform.Pos + new Vector3(12f, 10f, -20f), Explosion.SmokeBrown);

            Explosion.Create(api, Transform.Pos + new Vector3(0f, 12f, -22f), Explosion.SmokePoof);

            // Deactivate event in map
            api.EventMap.StoreTileEvent(originTile.X, originTile.Y, EventType.BirdCage, ActorInstantiationFlags.None, new ushort[] { type, 1 });
        }
    }
}