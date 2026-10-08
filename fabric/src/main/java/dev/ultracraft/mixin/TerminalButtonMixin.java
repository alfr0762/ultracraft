package dev.ultracraft.mixin;

import dev.ultracraft.UcTitleMenu;
import net.minecraft.client.gui.GuiGraphics;
import net.minecraft.client.gui.components.AbstractWidget;
import net.minecraft.client.gui.components.Button;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(Button.Plain.class)
public abstract class TerminalButtonMixin {
	@Inject(method = "renderContents", at = @At("HEAD"), cancellable = true)
	private void ultracraft$terminalButton(GuiGraphics graphics, int mouseX, int mouseY, float delta, CallbackInfo ci) {
		if (UcTitleMenu.paintButton((AbstractWidget) (Object) this, graphics)) ci.cancel();
	}
}
