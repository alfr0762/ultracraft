package dev.ultracraft.mixin;

import com.mojang.realmsclient.gui.screens.RealmsNotificationsScreen;
import dev.ultracraft.UcTitleMenu;
import net.minecraft.client.gui.Font;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.LogoRenderer;
import net.minecraft.client.gui.components.SplashRenderer;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.network.chat.Component;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Redirect;

/** Swap visual elements only: the original title-screen control and navigation flow still runs. */
@Mixin(TitleScreen.class)
public abstract class TitleScreenMixin extends Screen {
	protected TitleScreenMixin(Component title) { super(title); }

	@Redirect(method = "render", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/gui/screens/TitleScreen;renderPanorama(Lnet/minecraft/client/gui/GuiGraphics;F)V"))
	private void ultracraft$background(TitleScreen screen, GuiGraphics graphics, float delta) {
		if (UcTitleMenu.enabled()) UcTitleMenu.paintTitle(graphics, screen.width, screen.height);
		else super.renderPanorama(graphics, delta);
	}

	@Redirect(method = "render", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/gui/components/LogoRenderer;renderLogo(Lnet/minecraft/client/gui/GuiGraphics;IF)V"))
	private void ultracraft$logo(LogoRenderer logo, GuiGraphics graphics, int width, float alpha) {
		if (!UcTitleMenu.enabled()) logo.renderLogo(graphics, width, alpha);
	}

	@Redirect(method = "render", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/gui/components/SplashRenderer;render(Lnet/minecraft/client/gui/GuiGraphics;ILnet/minecraft/client/gui/Font;F)V"))
	private void ultracraft$splash(SplashRenderer splash, GuiGraphics graphics, int width, Font font, float alpha) {
		if (!UcTitleMenu.enabled()) splash.render(graphics, width, font, alpha);
	}

	@Redirect(method = "render", at = @At(value = "INVOKE", target = "Lcom/mojang/realmsclient/gui/screens/RealmsNotificationsScreen;render(Lnet/minecraft/client/gui/GuiGraphics;IIF)V"))
	private void ultracraft$realms(RealmsNotificationsScreen notifications, GuiGraphics graphics, int mouseX, int mouseY, float delta) {
		TitleScreen screen = (TitleScreen) (Object) this;
		int dx = UcTitleMenu.enabled() ? UcTitleMenu.realmsOffsetX(screen.width) : 0;
		int dy = UcTitleMenu.enabled() ? UcTitleMenu.realmsOffsetY(screen.height) : 0;
		graphics.pose().pushMatrix();
		graphics.pose().translate(dx, dy);
		notifications.render(graphics, mouseX - dx, mouseY - dy, delta);
		graphics.pose().popMatrix();
	}
}
