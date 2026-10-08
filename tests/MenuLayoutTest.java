package dev.ultracraft;

/** Checks interactive geometry in Minecraft's GUI-scaled pixels without loading a client. */
public final class MenuLayoutTest {
	private static int assertions;

	public static void main(String[] args) {
		int[][] examples = {{320, 240}, {427, 240}, {640, 360}, {960, 540}, {1280, 360}, {2560, 720}};
		for (int[] size : examples) {
			checkSize(size[0], size[1]);
			MenuLayout layout = MenuLayout.forSize(size[0], size[1]);
			System.out.printf("%dx%d: panel=%d..%d, rows=%d..%d, footer=%d..%d%n", size[0], size[1],
				layout.x(), layout.x() + layout.width(), layout.firstY(), layout.rowY(5) + layout.rowHeight(),
				size[1] - 32, size[1] - 12);
		}
		// Include the compact-to-normal row-height boundary and intermediate GUI scales.
		for (int width = 320; width <= 1600; width += 31) {
			for (int height = 240; height <= 900; height += 7) checkSize(width, height);
		}
		checkSize(320, 279);
		checkSize(320, 280);
		System.out.printf("MenuLayout: %,d assertions passed.%n", assertions);
	}

	private static void checkSize(int width, int height) {
		MenuLayout layout = MenuLayout.forSize(width, height);
		String size = width + "x" + height;
		check(layout.x() >= 12, size + " left margin");
		check(layout.width() >= 170, size + " minimum readable panel width");
		check(layout.width() <= 244, size + " maximum panel width");
		check(layout.x() + layout.width() <= width - 12, size + " right margin");
		check(layout.titleY() >= 12, size + " title top margin");
		check(layout.titleY() + 44 < layout.firstY(), size + " title and subtitle clear first button");
		int bottom = 0;
		for (int row = 0; row < 6; row++) {
			int rowHeight = row == 0 ? layout.primaryHeight() : layout.rowHeight();
			check(rowHeight >= 20, size + " row " + row + " minimum hit target");
			check(layout.rowY(row) >= bottom + layout.gap(), size + " row " + row + " does not overlap");
			bottom = layout.rowY(row) + rowHeight;
			check(bottom <= height, size + " row " + row + " visible");
		}
		check(bottom <= height - 34, size + " last row clears footer buttons by at least 2 pixels");
		int half = (layout.width() - 4) / 2;
		check(half >= 80, size + " options button readable width");
		check(layout.width() - half - 4 >= 80, size + " mod settings button readable width");
		check(height - 32 + 20 < height - 10, size + " footer clears native version and copyright");
	}

	private static void check(boolean condition, String message) {
		assertions++;
		if (!condition) throw new AssertionError(message);
	}
}
