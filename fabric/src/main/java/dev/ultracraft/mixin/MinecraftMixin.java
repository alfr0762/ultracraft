package dev.ultracraft.mixin;

import dev.ultracraft.Ultracraft;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.Options;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * As V1, Minecraft's keys still work (chat, commands, advancements, screenshots...), except where they would fight
 * ULTRAKILL for the same key. With guns out, clicks, number keys, E, Q and the wheel are ULTRAKILL's; with Minecraft
 * hands out (the hands key, V), they work as for Steve: attack and mine, use and place, hotbar, inventory, drop.
 * F is always the punch, so swap-offhand stays off. Perspective stays available for ULTRAKILL's V1 camera. A key rebound
 * to one of ULTRAKILL's controls (UcKeybindsScreen) is ULTRAKILL's too while the guns are out.
 */
@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	@Shadow @Final public Options options;

	@Inject(method = "handleKeybinds", at = @At("HEAD"), cancellable = true)
	private void ultracraft$keysToV1(CallbackInfo ci) {
		if (!Ultracraft.active) return;
		if (Ultracraft.uiMode) {
			ci.cancel();
			return;
		}
		ultracraft$off(options.keySwapOffhand);
		// at a shop's screen the clicks are the shop's (no mining or placing through it)
		if (Ultracraft.shopTouch) {
			ultracraft$off(options.keyAttack);
			ultracraft$off(options.keyUse);
		}
		if (!Ultracraft.hands) {
			ultracraft$off(options.keyAttack);
			ultracraft$off(options.keyUse);
			ultracraft$off(options.keyPickItem);
			ultracraft$off(options.keyDrop);
			ultracraft$off(options.keyInventory);
			for (KeyMapping k : options.keyHotbarSlots) ultracraft$off(k);
			// whatever Minecraft has on a key rebound to ULTRAKILL (but never Ultracraft's own keys: V1/Steve, hands)
			java.util.Set<String> taken = dev.ultracraft.UcKeybindsScreen.reboundKeys();
			if (!taken.isEmpty()) {
				for (KeyMapping k : options.keyMappings) {
					if (taken.contains(k.saveString()) && !dev.ultracraft.Ultracraft.isOwnKey(k)) ultracraft$off(k);
				}
			}
		}
	}

	private static void ultracraft$off(KeyMapping k) {
		while (k.consumeClick()) {
		}
		k.setDown(false);
	}

	/** As V1, each frame waits for ULTRAKILL's next one (UkFrame.waitForNext)... */
	@Inject(method = "runTick", at = @At(value = "INVOKE", target = "Lcom/mojang/blaze3d/platform/Window;updateDisplay(Lcom/mojang/blaze3d/TracyFrameCapture;)V", shift = At.Shift.AFTER))
	private void ultracraft$frameLock(boolean tick, CallbackInfo ci) {
		dev.ultracraft.UkFrame.waitForNext();
	}

	/** ...instead of Minecraft's own frame limit (ULTRAKILL's cap follows it, so the rate stays the same); when it isn't
	 * waiting (ULTRAKILL can't keep up), not far past ULTRAKILL's rate. */
	@org.spongepowered.asm.mixin.injection.Redirect(method = "runTick", at = @At(value = "INVOKE", target = "Lcom/mojang/blaze3d/platform/FramerateLimitTracker;getFramerateLimit()I"))
	private int ultracraft$noLimitInLockStep(com.mojang.blaze3d.platform.FramerateLimitTracker tracker) {
		return dev.ultracraft.UkFrame.locked ? 260 : dev.ultracraft.UkFrame.freeRunLimit(tracker.getFramerateLimit());
	}

	/** Esc closes ULTRAKILL's open menu (it is forwarded), so it must not also open Minecraft's pause screen. */
	@Inject(method = "pauseGame", at = @At("HEAD"), cancellable = true)
	private void ultracraft$escClosesV1Menu(boolean pauseOnly, CallbackInfo ci) {
		if (Ultracraft.active && Ultracraft.uiMode) ci.cancel();
	}
}
