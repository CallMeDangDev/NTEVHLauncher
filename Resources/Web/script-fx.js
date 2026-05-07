
function initParticles() {
    const c = document.getElementById('particleCanvas');
    if (!c) return;
    const ctx = c.getContext('2d');
    let W, H;
    const P = [];
    const N = 35;

    function resize() { W = c.width = innerWidth; H = c.height = innerHeight; }
    resize();
    addEventListener('resize', resize);

    class Dot {
        constructor() { this.reset(); }
        reset() {
            this.x = Math.random()*W;
            this.y = Math.random()*H;
            this.r = Math.random()*1.8+0.4;
            this.vx = (Math.random()-0.5)*0.25;
            this.vy = -Math.random()*0.35-0.05;
            this.a = Math.random()*0.4+0.08;
            this.da = (Math.random()>0.5?1:-1)*(Math.random()*0.004+0.001);
            const gold = Math.random()>0.35;
            this.R = gold?210:80; this.G = gold?170:195; this.B = gold?68:220;
        }
        tick() {
            this.x += this.vx; this.y += this.vy;
            this.a += this.da;
            if (this.a>0.55) this.da = -Math.abs(this.da);
            if (this.a<0.04) this.da = Math.abs(this.da);
            if (this.y<-10||this.x<-10||this.x>W+10) {
                this.x = Math.random()*W; this.y = H+10; this.a = 0.04;
            }
        }
        draw(ctx) {
            ctx.beginPath();
            ctx.arc(this.x, this.y, this.r, 0, Math.PI*2);
            ctx.fillStyle = `rgba(${this.R},${this.G},${this.B},${this.a})`;
            ctx.fill();
            if (this.r>1) {
                ctx.beginPath();
                ctx.arc(this.x, this.y, this.r*3, 0, Math.PI*2);
                ctx.fillStyle = `rgba(${this.R},${this.G},${this.B},${this.a*0.12})`;
                ctx.fill();
            }
        }
    }
    for (let i=0; i<N; i++) P.push(new Dot());
    (function loop() {
        ctx.clearRect(0,0,W,H);
        P.forEach(p => { p.tick(); p.draw(ctx); });
        requestAnimationFrame(loop);
    })();
}

let navWaveT = 0;      // global time tick
let _indCurL = 0, _indCurW = 0;   // currently rendered bounds (px from nav left)
let _indTgtL = 0, _indTgtW = 0;   // target bounds
let _indReady = false;

function initNavWave() {
    return;
}

function drawNavWave(canvas) {
    const ctx = canvas.getContext('2d');
    const W = canvas.width;
    const H = canvas.height;
    if (W <= 0 || H <= 0 || !_indReady) return;

    ctx.clearRect(0, 0, W, H);

    const cL = _indCurL;        // indicator left edge (lerped)
    const cW = _indCurW;        // indicator width (lerped)
    if (cW <= 1) return;

    const t = navWaveT;
    const arch = x => Math.sin(Math.PI * (x - cL) / cW);
    const breathe = 0.88 + 0.12 * Math.sin(t * 0.010);
    const MAIN_AMP = H * 0.12;

    const N = 4;

    const drawArc = (side) => {
        for (let i = 0; i < N; i++) {
            const scale  = (i + 1) / N;
            const amp    = MAIN_AMP * scale * breathe;
            const freq   = 0.044 + i * 0.007;
            const speed  = 0.030 + i * 0.004;
            const phase  = i * 0.85 + (side > 0 ? Math.PI : 0);
            const oscAmp = H * 0.015 * scale;

            const yLine  = x =>
                H * 0.72
                + side * amp * arch(x)
                + oscAmp * arch(x) * Math.sin((x - cL) * freq + t * speed + phase);

            const outerRatio = scale;
            const op   = 0.10 + outerRatio * 0.45;
            const lw   = 0.4  + outerRatio * 1.2;
            const blur = 2    + outerRatio * 8;

            ctx.save();
            ctx.shadowColor = `rgba(123,92,231,${op * 0.55})`;
            ctx.shadowBlur  = blur;
            ctx.beginPath();
            for (let x = cL; x <= cL + cW; x++) {
                x === cL ? ctx.moveTo(x, yLine(x)) : ctx.lineTo(x, yLine(x));
            }
            ctx.strokeStyle = `rgba(155,127,250,${op})`;
            ctx.lineWidth   = lw;
            ctx.stroke();
            ctx.restore();
        }
    };

    drawArc(-1);
    drawArc(+1);
}

function initMusicVisualizer() {
    const canvas = document.getElementById('musicCanvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    let W, H;
    function resize() { W = canvas.width = innerWidth; H = canvas.height = innerHeight; }
    resize();
    addEventListener('resize', resize);

    const spots = [
        { xR: 0.42, baseA: -0.22, c: [123, 92, 231], ph: 0.0, sp: 0.0018 },
        { xR: 0.52, baseA:  0.08, c: [155,127, 250], ph: 2.0, sp: 0.0015 },
        { xR: 0.61, baseA: -0.12, c: [ 56,189, 248], ph: 1.2, sp: 0.0012 },
        { xR: 0.71, baseA:  0.18, c: [155,127, 250], ph: 3.1, sp: 0.0015 },
        { xR: 0.81, baseA: -0.06, c: [123, 92, 231], ph: 0.8, sp: 0.0018 },
        { xR: 0.91, baseA:  0.24, c: [ 56,189, 248], ph: 1.7, sp: 0.0013 },
    ];

    const MAX_SPARKS = 50;
    const sparks = [];
    function mkSpark(fromLeft) {
        const yR = 0.10 + Math.random() * 0.72;   // full-height band: top 10% → bottom 82%
        return {
            x:       fromLeft ? -12 : Math.random() * W,
            y:       yR * H,
            yR,
            vx:      0.40 + Math.random() * 0.90,
            sinA:    Math.random() * Math.PI * 2,
            sinF:    0.016 + Math.random() * 0.024,
            sinAmp:  5 + Math.random() * 14,
            r:       0.80 + Math.random() * 1.00,  // star-sized: 0.80–1.80 px base
            hue:     220 + Math.random() * 70,   // purple(270-290) → blue(220-240)
            life:    fromLeft ? 0 : Math.floor(Math.random() * 260),
            maxLife: 300 + Math.floor(Math.random() * 300),
            freqBin: 8 + Math.floor(Math.random() * 72),
            active:  true,
        };
    }
    for (let i = 0; i < Math.floor(MAX_SPARKS / 2); i++) sparks.push(mkSpark(false));

    let bassE = 0, midE = 0, hiE = 0, overallE = 0;
    let t = 0;

    function getE(d, a, b) {
        let s = 0;
        for (let i = a; i < b && i < d.length; i++) s += d[i];
        return s / ((b - a) * 255);
    }

    function frame() {
        requestAnimationFrame(frame);
        t++;
        ctx.clearRect(0, 0, W, H);

        const an = window._musicAnalyser;
        let d = null;
        if (an) {
            d = new Uint8Array(an.frequencyBinCount);
            an.getByteFrequencyData(d);
        }

        const rBass = d ? getE(d, 0, 3)   : 0;
        const rMid  = d ? getE(d, 3, 25)  : 0;
        const rHi   = d ? getE(d, 25, 80) : 0;
        const rAll  = d ? getE(d, 0, 90)  : 0;

        bassE    += (rBass - bassE)    * 0.20;
        midE     += (rMid  - midE)     * 0.15;
        hiE      += (rHi   - hiE)      * 0.12;
        overallE += (rAll  - overallE) * 0.10;

        const playing = !!d;

        const targetCount = playing
            ? Math.round(8 + (bassE * 0.55 + midE * 0.30 + hiE * 0.15) * (MAX_SPARKS - 8))
            : 8;

        let activeCount = sparks.filter(s => s.active).length;
        if (activeCount < targetCount && sparks.length < MAX_SPARKS) {
            sparks.push(mkSpark(true));
        }
        if (activeCount > targetCount + 4) {
            for (let i = 0; i < sparks.length; i++) {
                if (sparks[i].active && sparks[i].life < sparks[i].maxLife - 30) {
                    sparks[i].maxLife = sparks[i].life + 30;
                    break;
                }
            }
        }

        spots.forEach(sp => {
            const bx    = sp.xR * W;
            const by    = H;
            const sway  = 0.06 + midE * 0.14;
            const curA  = sp.baseA + Math.sin(t * sp.sp + sp.ph) * sway;
            const hAng  = 0.042 + bassE * 0.040;
            const len   = H * 1.50;
            const en    = 0.18 + bassE * 0.45 + midE * 0.22;
            const op    = playing ? (0.025 + en * 0.065) : 0.012;

            const dx = Math.sin(curA);
            const dy = -Math.cos(curA);
            const tipX = bx + dx * len;
            const tipY = by + dy * len;

            const [r, g, b] = sp.c;
            const rr = Math.min(255, r + midE * 30) | 0;
            const gg = Math.min(255, g + hiE  * 20) | 0;
            const bb = Math.min(255, b + bassE * 40) | 0;

            const grad = ctx.createLinearGradient(bx, by, tipX, tipY);
            grad.addColorStop(0.00, `rgba(${rr},${gg},${bb},${(op * 4.2).toFixed(3)})`);
            grad.addColorStop(0.15, `rgba(${rr},${gg},${bb},${(op * 2.3).toFixed(3)})`);
            grad.addColorStop(0.50, `rgba(${rr},${gg},${bb},${(op * 0.70).toFixed(3)})`);
            grad.addColorStop(1.00, `rgba(${rr},${gg},${bb},0)`);

            const la = curA - hAng;
            const ra = curA + hAng;
            ctx.beginPath();
            ctx.moveTo(bx, by);
            ctx.lineTo(bx + Math.sin(la) * len, by - Math.cos(la) * len);
            ctx.lineTo(bx + Math.sin(ra) * len, by - Math.cos(ra) * len);
            ctx.closePath();
            ctx.fillStyle = grad;
            ctx.fill();
        });

        for (let i = sparks.length - 1; i >= 0; i--) {
            const sp = sparks[i];
            sp.life++;
            // speed = base × (bin energy of this spark + global bass)
            const binNow   = d ? d[sp.freqBin] / 255 : 0.15;
            const speedMul = 1.0 + binNow * 3.5 + bassE * 1.8;
            sp.x    += sp.vx * speedMul;
            sp.sinA += sp.sinF;
            sp.y     = sp.yR * H + Math.sin(sp.sinA) * sp.sinAmp;

            if (sp.x > W + 18 || sp.life > sp.maxLife) {
                sparks.splice(i, 1);
                continue;
            }

            const progress = sp.life / sp.maxLife;
            const fade = Math.min(progress * 6, 1, (1 - progress) * 5);
            if (fade < 0.01) continue;

            const binVal = d ? d[sp.freqBin] / 255
                             : (0.15 + 0.10 * Math.sin(t * 0.04 + sp.freqBin));
            const radius = sp.r;
            const alpha  = fade * (0.55 + binVal * 0.45);
            const bloomMul  = 1.0 + binVal * 5.0 + bassE * 6.0 + midE * 3.0;
            const bloomAlpha = fade * (0.08 + binVal * 0.28 + bassE * 0.22);
            const hue    = Math.min(290, Math.max(220,
                sp.hue + bassE * 25 - hiE * 22 + Math.sin(t * 0.008 + sp.freqBin) * 12
            ));

            ctx.beginPath();
            ctx.arc(sp.x, sp.y, radius, 0, Math.PI * 2);
            ctx.fillStyle = `hsla(${hue}, 95%, 95%, ${alpha})`;
            ctx.fill();

            const bloom = radius * bloomMul;
            const g = ctx.createRadialGradient(sp.x, sp.y, 0, sp.x, sp.y, bloom);
            g.addColorStop(0.0, `hsla(${hue}, 85%, 80%, ${bloomAlpha})`);
            g.addColorStop(0.4, `hsla(${hue}, 75%, 68%, ${bloomAlpha * 0.35})`);
            g.addColorStop(1.0, `hsla(${hue}, 65%, 55%, 0)`);
            ctx.beginPath();
            ctx.arc(sp.x, sp.y, bloom, 0, Math.PI * 2);
            ctx.fillStyle = g;
            ctx.fill();
        }

        if (d && playing) {
            const BAR_N  = 80;
            const totalW = W * 0.62;
            const x0     = (W - totalW) / 2;
            const bw     = totalW / BAR_N;
            const maxH   = H * 0.18;
            const baseY  = H - 54;

            for (let i = 0; i < BAR_N; i++) {
                const binIdx = Math.floor(i * 92 / BAR_N);
                const val    = d[binIdx] / 255;
                const bh     = val * maxH;
                if (bh < 1.8) continue;
                const hue   = 253 + i * 1.1 - bassE * 35;
                const alpha = 0.07 + val * 0.30;
                const x     = x0 + i * bw;
                ctx.fillStyle = `hsla(${hue}, 72%, 72%, ${alpha})`;
                ctx.fillRect(x, baseY - bh, bw - 1.2, bh);
                ctx.fillStyle = `hsla(${hue}, 72%, 72%, ${alpha * 0.22})`;
                ctx.fillRect(x, baseY, bw - 1.2, bh * 0.35);
            }
        }

        if (overallE > 0.06 && playing) {
            const glow = overallE * 0.13;
            const eg = ctx.createRadialGradient(W * 0.60, H * 0.50, H * 0.15, W * 0.60, H * 0.50, W * 0.70);
            eg.addColorStop(0.0, `rgba(0,0,0,0)`);
            eg.addColorStop(0.7, `rgba(0,0,0,0)`);
            eg.addColorStop(1.0, `rgba(123,92,231,${glow})`);
            ctx.fillStyle = eg;
            ctx.fillRect(0, 0, W, H);
        }
    }

    requestAnimationFrame(frame);
}
