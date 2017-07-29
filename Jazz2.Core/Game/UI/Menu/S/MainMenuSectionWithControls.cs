﻿using System;
using Duality;
using Duality.Drawing;
using Duality.Input;
using static Jazz2.Settings;

namespace Jazz2.Game.UI.Menu.S
{
    public class MainMenuSectionWithControls : MainMenuSection
    {
        protected MenuControlBase[] controls;

        private int selectedIndex;
        private float animation;

        public MainMenuSectionWithControls()
        {
        }

        public override void OnShow(MainMenu root)
        {
            animation = 0f;

            base.OnShow(root);
        }

        public override void OnPaint(IDrawDevice device, Canvas c)
        {
            if (controls == null) {
                return;
            }

            Vector2 center = device.TargetSize * 0.5f;
            center.Y *= 0.8f;

            for (int i = 0; i < controls.Length; i++) {
                controls[i].OnDraw(device, c, ref center, selectedIndex == i);
            }
        }

        public override void OnUpdate()
        {
            if (controls == null) {
                return;
            }

            if (animation < 1f) {
                animation = Math.Min(animation + Time.TimeMult * 0.016f, 1f);
            }

            controls[selectedIndex].OnUpdate();

            if (!controls[selectedIndex].IsInputCaptured) {
                if (DualityApp.Keyboard.KeyHit(Key.Enter)) {
                    //
                } else if (DualityApp.Keyboard.KeyHit(Key.Up)) {
                    api.PlaySound("MenuSelect", 0.4f);
                    animation = 0f;
                    if (selectedIndex > 0) {
                        selectedIndex--;
                    } else {
                        selectedIndex = controls.Length - 1;
                    }
                } else if (DualityApp.Keyboard.KeyHit(Key.Down)) {
                    api.PlaySound("MenuSelect", 0.4f);
                    animation = 0f;
                    if (selectedIndex < controls.Length - 1) {
                        selectedIndex++;
                    } else {
                        selectedIndex = 0;
                    }
                } else if (DualityApp.Keyboard.KeyHit(Key.Escape)) {
                    api.PlaySound("MenuSelect", 0.5f);
                    api.LeaveSection(this);
                }
            }
        }
    }
}