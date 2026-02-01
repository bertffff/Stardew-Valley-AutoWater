using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace AutoWaterBot
{
    public class ModEntry : Mod
    {
        private bool isBotActive = false;
        private Texture2D buttonTexture;
        private Rectangle buttonRect = new Rectangle(20, 100, 64, 64);

        // Состояния
        private enum BotState { Idle, MovingToCrop, Watering, MovingToWater, Refilling }
        private BotState currentState = BotState.Idle;
        
        private Vector2 targetTile = Vector2.Zero;
        private int actionTimer = 0;

        public override void Entry(IModHelper helper)
        {
            helper.Events.Display.RenderedHud += OnRenderedHud;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }

        private void OnRenderedHud(object sender, RenderedHudEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // Создаем текстуру "на лету", если её нет
            if (buttonTexture == null)
            {
                buttonTexture = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
                buttonTexture.SetData(new[] { Color.White });
            }

            // Рисуем кнопку (Красная = выкл, Зеленая = вкл)
            Color color = isBotActive ? Color.LimeGreen : Color.Red;
            e.SpriteBatch.Draw(buttonTexture, buttonRect, color);
            
            // Рисуем обводку
            e.SpriteBatch.Draw(buttonTexture, new Rectangle(buttonRect.X, buttonRect.Y, 64, 4), Color.Black);
            e.SpriteBatch.Draw(buttonTexture, new Rectangle(buttonRect.X, buttonRect.Y + 60, 64, 4), Color.Black);
            e.SpriteBatch.Draw(buttonTexture, new Rectangle(buttonRect.X, buttonRect.Y, 4, 64), Color.Black);
            e.SpriteBatch.Draw(buttonTexture, new Rectangle(buttonRect.X + 60, buttonRect.Y, 4, 64), Color.Black);

            string status = isBotActive ? "ON" : "OFF";
            e.SpriteBatch.DrawString(Game1.smallFont, $"BOT: {status}", new Vector2(buttonRect.X, buttonRect.Y + 70), Color.White);
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (e.Button == SButton.MouseLeft)
            {
                Point cursor = Game1.getMousePosition();
                if (buttonRect.Contains(cursor))
                {
                    isBotActive = !isBotActive;
                    currentState = BotState.Idle;
                    Game1.playSound("drumkit6");
                    this.Monitor.Log($"Bot is now {(isBotActive ? "Active" : "Inactive")}", LogLevel.Debug);
                }
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || !isBotActive) return;

            // Если персонаж уже двигается сам или идет анимация — ждем
            if (Game1.player.isMoving() || Game1.player.UsingTool) return;

            if (actionTimer > 0)
            {
                actionTimer--;
                return;
            }

            Farmer player = Game1.player;

            // Проверка инструмента
            if (player.CurrentTool is not WateringCan can)
            {
                Game1.addHUDMessage(new HUDMessage("Hold a Watering Can!", 3));
                isBotActive = false;
                return;
            }

            switch (currentState)
            {
                case BotState.Idle:
                    DecideNextMove(player, can);
                    break;

                case BotState.MovingToCrop:
                case BotState.MovingToWater:
                    MoveTowardsTarget();
                    // Проверяем дистанцию (1.5 клетки)
                    if (Vector2.Distance(player.getTileLocation(), targetTile) <= 1.1f) 
                    {
                        Game1.player.Halt();
                        FaceTarget();
                        currentState = (currentState == BotState.MovingToCrop) ? BotState.Watering : BotState.Refilling;
                        actionTimer = 10; // Небольшая пауза перед действием
                    }
                    break;

                case BotState.Watering:
                    DoWaterAction(player, can);
                    currentState = BotState.Idle;
                    break;

                case BotState.Refilling:
                    DoWaterAction(player, can);
                    // После наполнения сбрасываем состояние
                    can.WaterLeft = can.waterCanMax; 
                    currentState = BotState.Idle;
                    break;
            }
        }

        private void DoWaterAction(Farmer player, WateringCan can)
        {
            // Эмуляция нажатия
            Vector2 toolLoc = new Vector2(targetTile.X * 64f, targetTile.Y * 64f);
            
            // Выполняем анимацию
            player.animateOnce(208 + player.FacingDirection);
            
            // Используем инструмент
            can.DoFunction(player.currentLocation, (int)toolLoc.X, (int)toolLoc.Y, 1, player);
            
            actionTimer = 45; // Ждем завершения анимации
        }

        private void DecideNextMove(Farmer player, WateringCan can)
        {
            // 1. Нужна вода?
            if (can.WaterLeft <= 0)
            {
                Vector2? waterSpot = FindNearestWater(player);
                if (waterSpot.HasValue)
                {
                    targetTile = waterSpot.Value;
                    currentState = BotState.MovingToWater;
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage("No water nearby!", 3));
                    isBotActive = false;
                }
                return;
            }

            // 2. Ищем грядку
            Vector2? cropSpot = FindNearestUnwateredCrop(player);
            if (cropSpot.HasValue)
            {
                targetTile = cropSpot.Value;
                currentState = BotState.MovingToCrop;
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage("Done!", 2));
                isBotActive = false;
            }
        }

        private Vector2? FindNearestUnwateredCrop(Farmer player)
        {
            GameLocation loc = player.currentLocation;
            Vector2 playerPos = player.getTileLocation();
            double minDistance = double.MaxValue;
            Vector2? bestSpot = null;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt)
                {
                    // 0 = сухо, 1 = полито. Нам нужно 0.
                    // Также проверяем, есть ли там вообще посевы (dirt.crop != null), чтобы не поливать пустую землю
                    if (dirt.state.Value == 0 && dirt.crop != null) 
                    {
                        double dist = Vector2.Distance(playerPos, pair.Key);
                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            bestSpot = pair.Key;
                        }
                    }
                }
            }
            return bestSpot;
        }

        private Vector2? FindNearestWater(Farmer player)
        {
            GameLocation loc = player.currentLocation;
            Vector2 playerPos = player.getTileLocation();
            int radius = 20;

            // Простой поиск по спирали или квадрату
            double minDst = double.MaxValue;
            Vector2? bestWater = null;

            for (int x = (int)playerPos.X - radius; x <= playerPos.X + radius; x++)
            {
                for (int y = (int)playerPos.Y - radius; y <= playerPos.Y + radius; y++)
                {
                    // Если это вода
                    if (loc.isOpenWater(x, y) || loc.isWaterTile(x, y))
                    {
                        // Проверяем 4 клетки вокруг воды - можно ли туда встать?
                        Vector2[] adjs = { new Vector2(x+1, y), new Vector2(x-1, y), new Vector2(x, y+1), new Vector2(x, y-1) };
                        foreach(var adj in adjs)
                        {
                            if (loc.isTileLocationTotallyClearAndPlaceable(adj) && !loc.isWaterTile((int)adj.X, (int)adj.Y))
                            {
                                double dst = Vector2.Distance(playerPos, adj);
                                if (dst < minDst)
                                {
                                    minDst = dst;
                                    bestWater = adj; // Идем на сушу РЯДОМ с водой
                                }
                            }
                        }
                    }
                }
            }
            return bestWater;
        }

        private void MoveTowardsTarget()
        {
            Farmer player = Game1.player;
            Vector2 current = player.getTileLocation();
            
            // Очень примитивное движение: сначала выравниваем X, потом Y
            // Внимание: бот застрянет в заборе! Стойте на открытой местности.
            
            float speed = 0.1f; // Порог чувствительности
            
            if (current.X + speed < targetTile.X) player.SetMovingRight(true);
            else if (current.X - speed > targetTile.X) player.SetMovingLeft(true);
            else if (current.Y + speed < targetTile.Y) player.SetMovingDown(true);
            else if (current.Y - speed > targetTile.Y) player.SetMovingUp(true);
        }
        
        private void FaceTarget()
        {
            Vector2 playerPos = Game1.player.getTileLocation();
            // Разница координат
            float dx = targetTile.X - playerPos.X;
            float dy = targetTile.Y - playerPos.Y;

            if (Math.Abs(dx) > Math.Abs(dy))
                Game1.player.faceDirection(dx > 0 ? 1 : 3); // 1=Right, 3=Left
            else
                Game1.player.faceDirection(dy > 0 ? 2 : 0); // 2=Down, 0=Up
        }
    }
}
