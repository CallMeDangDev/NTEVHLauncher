

const S = {
    page: 'home',
    installing: false, installed: false,
    gamePath: '',
    cfg: { gamePath:'', linuxMode: false },
    autoCheckDone: false
};

const bridge = () => window.chrome?.webview?.hostObjects?.launcher;

document.addEventListener('DOMContentLoaded', () => {
    initParticles();
    initMusicVisualizer();
    initTopBar();
    initTopNav();
    initBottomBar();
    initAudioPlayer();
    initWaterRipple();
    initFontCreator();
    initSidePanel();
    loadSettings();
    loadVersions();
    loadReleaseNotes();
});

