// The HUD lays itself out in CSS pixels like the old viewer page; Unity's Screen.width is the canvas in device pixels.
mergeInto(LibraryManager.library, {
  PezHud_InnerWidth: function () { return window.innerWidth || 0; },
  PezHud_ScreenWidth: function () { return (window.screen && window.screen.width) || 0; },
});
