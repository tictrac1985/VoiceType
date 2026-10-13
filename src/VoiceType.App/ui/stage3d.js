// =====================================================================
//  صدى — المجسمات ثلاثية الأبعاد في صفحة الإملاء
//  لكل ثيم مشهد: ميكروفون استوديو تحت الضوء · كرة زجاجية · جهاز خزفي.
//  المشهد يتفاعل مع حالة الإملاء (جاهز / يسجّل / يكتب) ومع مستوى الصوت.
//  يرسم بسرعة كاملة فقط عند الحاجة، ويتوقف عندما تكون النافذة مخفية.
// =====================================================================
import * as THREE from 'three';
import { RoomEnvironment } from './three-room.js';
import { RoundedBoxGeometry } from './three-roundedbox.js';

const host = document.getElementById('stage');
let renderer;
try {
  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: 'low-power' });
} catch (e) {
  renderer = null; // لا يوجد WebGL: تبقى الصورة البديلة
}

if (renderer) {
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.75));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  host.prepend(renderer.domElement);

  const pmrem = new THREE.PMREMGenerator(renderer);
  const envTexture = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;

  const live = { state: 'Idle', level: 0, smooth: 0, hover: false, busyUntil: 0 };
  const builders = { spotlight: buildSpotlight, orb: buildOrb, monolith: buildMonolith };
  let current = null;

  function setTheme(name) {
    const build = builders[name] || builders.spotlight;
    if (current?.name === name) return;
    current?.dispose();
    current = build();
    current.name = name;
    resize();
    kick(1500);
  }

  function resize() {
    const w = host.clientWidth, h = host.clientHeight;
    if (!w || !h) return;
    renderer.setSize(w, h, false);
    if (current) {
      current.camera.aspect = w / h;
      current.frame?.(w / h);
      current.camera.updateProjectionMatrix();
    }
    kick(300);
  }

  function kick(ms) { live.busyUntil = Math.max(live.busyUntil, performance.now() + ms); }

  window.Stage3D = {
    setTheme,
    setState(s) { live.state = s || 'Idle'; kick(1500); },
    setLevel(v) { live.level = Math.max(0, Math.min(1, (v || 0) * 7)); kick(400); },
  };

  new ResizeObserver(resize).observe(host);
  host.parentElement.addEventListener('pointerenter', () => { live.hover = true; kick(800); });
  host.parentElement.addEventListener('pointerleave', () => { live.hover = false; });
  document.addEventListener('visibilitychange', () => kick(500));

  // حلقة الرسم: كاملة أثناء التسجيل/المعالجة/التفاعل، وبطيئة جدًا وقت الراحة
  let last = 0;
  function loop(now) {
    requestAnimationFrame(loop);
    if (document.hidden || !current || !host.offsetParent) return;
    const active = live.state !== 'Idle' || live.hover || now < live.busyUntil;
    const interval = active ? 0 : 1000 / 14;
    if (now - last < interval) return;
    const dt = Math.min(0.05, (now - last) / 1000 || 0.016);
    last = now;
    live.smooth += ((live.state === 'Recording' ? live.level : 0) - live.smooth) * Math.min(1, dt * 10);
    live.level *= 0.9;
    current.update(now / 1000, dt, live);
    renderer.render(current.scene, current.camera);
  }

  const start = window.__stage || {};
  live.state = start.state || 'Idle';
  setTheme(start.theme || document.documentElement.dataset.theme || 'spotlight');
  host.classList.add('ready');
  requestAnimationFrame(loop);

  // ===================================================================
  //  أدوات مشتركة
  // ===================================================================
  function baseScene(exposure, envIntensity) {
    const scene = new THREE.Scene();
    scene.environment = envTexture;
    scene.environmentIntensity = envIntensity;
    renderer.toneMappingExposure = exposure;
    const disposables = [];
    const track = o => { disposables.push(o); return o; };
    const dispose = () => {
      scene.traverse(o => { o.geometry?.dispose(); [].concat(o.material || []).forEach(m => m.dispose()); });
      disposables.forEach(d => d.dispose?.());
    };
    return { scene, track, dispose };
  }

  function canvasTexture(w, h, draw) {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    draw(c.getContext('2d'), w, h);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
  }

  function spotShadow(light, size = 1024) {
    light.castShadow = true;
    light.shadow.mapSize.set(size, size);
    light.shadow.radius = 5;
    light.shadow.bias = -0.0005;
  }

  // ===================================================================
  //  ١) تحت الضوء: ميكروفون استوديو معدني بحلقات ذهبية تحت ضوء سبوت
  // ===================================================================
  function buildSpotlight() {
    const { scene, track, dispose } = baseScene(1.05, 0.35);
    scene.fog = new THREE.Fog(0x030303, 8, 18);
    const camera = new THREE.PerspectiveCamera(28, 1, 0.1, 100);
    const look = new THREE.Vector3(0, 1.45, 0);

    const floor = new THREE.Mesh(new THREE.CircleGeometry(12, 64), new THREE.MeshStandardMaterial({ color: 0x0b0a09, roughness: 0.6, metalness: 0.15 }));
    floor.rotation.x = -Math.PI / 2;
    floor.receiveShadow = true;
    scene.add(floor);

    const grilleTex = track(canvasTexture(512, 512, (g, w, h) => {
      g.fillStyle = '#bdbdbd'; g.fillRect(0, 0, w, h); g.fillStyle = '#0a0a0a';
      for (let y = 0; y < h; y += 8) for (let x = (y / 8) % 2 ? 4 : 0; x < w; x += 8) { g.beginPath(); g.arc(x, y, 2.6, 0, 7); g.fill(); }
    }));
    grilleTex.wrapS = grilleTex.wrapT = THREE.RepeatWrapping;
    grilleTex.repeat.set(6, 3);
    const gold = new THREE.MeshStandardMaterial({ color: 0xe2ac58, metalness: 1, roughness: 0.22 });

    const mic = new THREE.Group();
    scene.add(mic);
    const add = (mesh, y) => { mesh.position.y = y; mesh.castShadow = true; mic.add(mesh); return mesh; };
    add(new THREE.Mesh(new THREE.CapsuleGeometry(0.62, 0.95, 24, 64), new THREE.MeshStandardMaterial({ color: 0xd0d0d0, map: grilleTex, metalness: 0.95, roughness: 0.38, bumpMap: grilleTex, bumpScale: 3 })), 2.55);
    add(new THREE.Mesh(new THREE.CylinderGeometry(0.66, 0.66, 0.22, 64), gold), 2.42);
    add(new THREE.Mesh(new THREE.CylinderGeometry(0.5, 0.44, 1.35, 64), new THREE.MeshStandardMaterial({ color: 0x111113, metalness: 0.6, roughness: 0.38 })), 1.6);
    add(new THREE.Mesh(new THREE.CylinderGeometry(0.505, 0.505, 0.04, 64), gold), 1.85);
    const ring = add(new THREE.Mesh(new THREE.TorusGeometry(0.95, 0.045, 24, 96), gold), 1.75);
    ring.rotation.x = Math.PI / 2;
    for (const a of [0, Math.PI]) {
      const arm = add(new THREE.Mesh(new THREE.CylinderGeometry(0.035, 0.035, 0.95, 16), gold), 1.3);
      arm.position.x = Math.cos(a) * 0.95; arm.position.z = Math.sin(a) * 0.95;
    }
    const yoke = add(new THREE.Mesh(new THREE.TorusGeometry(0.95, 0.04, 16, 64, Math.PI), gold), 0.85);
    yoke.rotation.x = Math.PI;
    add(new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 0.85, 24), new THREE.MeshStandardMaterial({ color: 0x1a1a1c, metalness: 0.8, roughness: 0.3 })), 0.42);
    const base = add(new THREE.Mesh(new THREE.CylinderGeometry(0.9, 1.0, 0.14, 64), new THREE.MeshStandardMaterial({ color: 0x121214, metalness: 0.7, roughness: 0.35 })), 0.07);
    base.receiveShadow = true;
    const baseRim = new THREE.Mesh(new THREE.TorusGeometry(0.95, 0.02, 12, 96), gold);
    baseRim.rotation.x = Math.PI / 2; baseRim.position.y = 0.14; mic.add(baseRim);
    mic.rotation.y = -0.35;

    const spot = new THREE.SpotLight(0xfff1d6, 180, 20, Math.PI / 9, 0.55, 1.6);
    spot.position.set(0.3, 9, 1.2); spot.target.position.set(0, 1.2, 0);
    spotShadow(spot);
    scene.add(spot, spot.target);
    const rim = new THREE.PointLight(0xe9a64a, 14, 10, 1.8); rim.position.set(-3, 2.6, -2.5); scene.add(rim);
    const rim2 = new THREE.PointLight(0x6d8cff, 4, 10, 1.8); rim2.position.set(3.2, 2, -2); scene.add(rim2);
    scene.add(new THREE.AmbientLight(0xffffff, 0.05));

    const coneMat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
      uniforms: { tint: { value: new THREE.Color(1, 0.9, 0.72) }, power: { value: 0.09 } },
      vertexShader: 'varying float vY;varying vec3 vN;varying vec3 vV;void main(){vY=position.y;vec4 mv=modelViewMatrix*vec4(position,1.);vN=normalize(normalMatrix*normal);vV=normalize(-mv.xyz);gl_Position=projectionMatrix*mv;}',
      fragmentShader: 'uniform vec3 tint;uniform float power;varying float vY;varying vec3 vN;varying vec3 vV;void main(){float t=clamp((vY+4.)/8.,0.,1.);float e=pow(abs(dot(vN,vV)),2.);gl_FragColor=vec4(tint*power*t*e,1.);}',
    });
    const cone = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 2.3, 8, 64, 1, true), coneMat);
    cone.position.set(0.15, 4.6, 0.5);
    scene.add(cone);

    const haloMat = new THREE.MeshBasicMaterial({ color: 0xe9b45c, transparent: true, opacity: 0.55 });
    const halo = new THREE.Mesh(new THREE.RingGeometry(1.25, 1.3, 128), haloMat);
    halo.rotation.x = -Math.PI / 2; halo.position.y = 0.005; scene.add(halo);
    const wave = new THREE.Mesh(new THREE.RingGeometry(1.3, 1.34, 128), new THREE.MeshBasicMaterial({ color: 0xe9b45c, transparent: true, opacity: 0 }));
    wave.rotation.x = -Math.PI / 2; wave.position.y = 0.006; scene.add(wave);

    const warm = new THREE.Color(0xfff1d6), red = new THREE.Color(0xff6a4a), amber = new THREE.Color(0xffc46b);
    const goldColor = new THREE.Color(0xe9b45c), redColor = new THREE.Color(0xff4d3a);

    return {
      scene, camera, dispose,
      frame(aspect) {
        // المجسم دائمًا كامل داخل الإطار مهما تغيّر شكل المسرح
        // الحالة مكتوبة على اليمين، فنضع الميكروفون يسار المسرح
        const dist = aspect < 1.1 ? 12 : 10.5;
        const side = 0.75 + Math.max(0, aspect - 1.2) * 1.4;
        camera.position.set(side - 0.7, 2.4, dist);
        look.set(side, 1.45, 0);
        camera.lookAt(look);
      },
      update(t, dt, s) {
        const rec = s.state === 'Recording', busy = s.state === 'Processing';
        mic.rotation.y = -0.35 + Math.sin(t * 0.4) * 0.12 + (busy ? t * 1.2 % (Math.PI * 2) * 0 : 0);
        if (busy) mic.rotation.y += Math.sin(t * 3) * 0.08;
        const target = rec ? red : busy ? amber : warm;
        spot.color.lerp(target, Math.min(1, dt * 4));
        spot.intensity = 180 + (rec ? 40 + s.smooth * 160 : 0) + Math.sin(t * 2.1) * 4;
        coneMat.uniforms.tint.value.copy(spot.color);
        coneMat.uniforms.power.value = 0.08 + (rec ? s.smooth * 0.08 : 0);
        haloMat.color.lerp(rec ? redColor : goldColor, Math.min(1, dt * 4));
        haloMat.opacity = 0.45 + (rec ? s.smooth * 0.5 : busy ? Math.sin(t * 6) * 0.25 + 0.25 : 0);
        const phase = (t * (rec ? 0.9 : 0.35)) % 1;
        wave.scale.setScalar(1 + phase * (rec ? 1.6 : 0.8));
        wave.material.color.copy(haloMat.color);
        wave.material.opacity = (1 - phase) * (rec ? 0.5 : busy ? 0.3 : 0.12);
      },
    };
  }

  // ===================================================================
  //  ٢) الكرة الزجاجية: كرة شفافة فيها ميكروفون مضيء فوق قاعدة
  // ===================================================================
  function buildOrb() {
    const { scene, dispose } = baseScene(1.1, 0.3);
    const camera = new THREE.PerspectiveCamera(30, 1, 0.1, 100);
    const look = new THREE.Vector3(0, 1.25, 0);

    const floor = new THREE.Mesh(new THREE.CircleGeometry(14, 96), new THREE.MeshStandardMaterial({ color: 0x02080a, roughness: 0.6, metalness: 0.1, envMapIntensity: 0, transparent: true, opacity: 0.9 }));
    floor.rotation.x = -Math.PI / 2; floor.receiveShadow = true; scene.add(floor);
    const rings = [];
    for (let i = 0; i < 6; i++) {
      const r = new THREE.Mesh(new THREE.RingGeometry(1.4 + i * 0.6, 1.415 + i * 0.6, 160), new THREE.MeshBasicMaterial({ color: 0x5eead4, transparent: true, opacity: 0.4 - i * 0.06 }));
      r.rotation.x = -Math.PI / 2; r.position.y = 0.003; scene.add(r); rings.push(r);
    }

    const orb = new THREE.Mesh(new THREE.SphereGeometry(0.92, 96, 96), new THREE.MeshPhysicalMaterial({
      color: 0xffffff, transmission: 1, thickness: 1.6, roughness: 0.05, ior: 1.45, iridescence: 1, iridescenceIOR: 1.3,
      iridescenceThicknessRange: [200, 800], clearcoat: 1, attenuationColor: new THREE.Color(0x5eead4), attenuationDistance: 2.2,
    }));
    orb.castShadow = true; scene.add(orb);
    const core = new THREE.Group(); scene.add(core);
    const coreMat = new THREE.MeshStandardMaterial({ color: 0xbffcf3, emissive: 0x2dd4bf, emissiveIntensity: 1.4, roughness: 0.3 });
    const cap = new THREE.Mesh(new THREE.CapsuleGeometry(0.17, 0.34, 12, 32), coreMat); cap.position.y = 0.12; core.add(cap);
    const arc = new THREE.Mesh(new THREE.TorusGeometry(0.32, 0.035, 12, 64, Math.PI), coreMat); arc.rotation.z = Math.PI; arc.position.y = 0.05; core.add(arc);
    const stem = new THREE.Mesh(new THREE.CylinderGeometry(0.035, 0.035, 0.22, 12), coreMat); stem.position.y = -0.37; core.add(stem);
    core.scale.setScalar(0.55);
    const glow = new THREE.PointLight(0x5eead4, 8, 6, 2); scene.add(glow);

    const ped = new THREE.Mesh(new THREE.CylinderGeometry(0.75, 0.95, 0.32, 96), new THREE.MeshStandardMaterial({ color: 0x0a1d24, metalness: 0.8, roughness: 0.25 }));
    ped.position.y = 0.16; ped.castShadow = true; ped.receiveShadow = true; scene.add(ped);
    const lipMat = new THREE.MeshBasicMaterial({ color: 0x5eead4 });
    const lip = new THREE.Mesh(new THREE.TorusGeometry(0.76, 0.018, 12, 128), lipMat); lip.rotation.x = Math.PI / 2; lip.position.y = 0.33; scene.add(lip);

    const key = new THREE.SpotLight(0xd9fbff, 70, 22, Math.PI / 11, 0.7, 1.5);
    key.position.set(-2, 9, 3); key.target.position.set(0, 1, 0); spotShadow(key); scene.add(key, key.target);
    const coral = new THREE.PointLight(0xff8f70, 16, 12, 1.6); coral.position.set(4.5, 3.2, -3); scene.add(coral);
    const teal = new THREE.PointLight(0x2dd4bf, 10, 12, 1.6); teal.position.set(-4.5, 3, -3); scene.add(teal);

    const pg = new THREE.BufferGeometry(); const pts = [];
    for (let i = 0; i < 160; i++) pts.push((Math.random() - 0.5) * 12, Math.random() * 5, (Math.random() - 0.5) * 5 - 2);
    pg.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
    const dust = new THREE.Points(pg, new THREE.PointsMaterial({ color: 0x9ff5ea, size: 0.03, transparent: true, opacity: 0.6 }));
    scene.add(dust);

    const tealC = new THREE.Color(0x5eead4), coralC = new THREE.Color(0xff8f70), coreTeal = new THREE.Color(0x2dd4bf), coreRed = new THREE.Color(0xff6b5a);

    return {
      scene, camera, dispose,
      frame(aspect) {
        const dist = aspect < 1.1 ? 10.5 : 9;
        const side = aspect > 1.25 ? 0.6 + (aspect - 1.25) * 1.3 : 0.35;
        camera.position.set(side, 2.4, dist);
        look.set(side, 1.25, 0);
        camera.lookAt(look);
      },
      update(t, dt, s) {
        const rec = s.state === 'Recording', busy = s.state === 'Processing';
        const y = 1.75 + Math.sin(t * 1.2) * 0.06;
        orb.position.y = y; core.position.y = y; glow.position.y = y;
        orb.rotation.y += dt * (busy ? 1.6 : 0.15);
        core.rotation.y = Math.sin(t * 0.7) * 0.4 + (busy ? t * 2 : 0);
        coreMat.emissive.lerp(rec ? coreRed : coreTeal, Math.min(1, dt * 4));
        coreMat.emissiveIntensity = 1.3 + (rec ? 0.6 + s.smooth * 3 : busy ? Math.sin(t * 6) * 0.6 + 0.6 : 0);
        glow.color.copy(coreMat.emissive);
        glow.intensity = 7 + (rec ? s.smooth * 25 : 0);
        lipMat.color.lerp(rec ? coralC : tealC, Math.min(1, dt * 4));
        rings.forEach((r, i) => {
          const pulse = rec ? Math.max(0, Math.sin(t * 5 - i * 0.7)) * s.smooth : busy ? Math.max(0, Math.sin(t * 4 - i * 0.9)) * 0.5 : 0;
          r.material.opacity = Math.max(0.03, 0.38 - i * 0.06 + pulse * 0.6);
          r.material.color.copy(lipMat.color);
        });
        dust.rotation.y = t * 0.02;
      },
    };
  }

  // ===================================================================
  //  ٣) الخزف: جهاز خزفي بزر نحاسي في ضوء غروب وظلال طويلة
  // ===================================================================
  function buildMonolith() {
    const { scene, track, dispose } = baseScene(1.0, 0.25);
    scene.fog = new THREE.Fog(0x2a1209, 10, 26);
    const camera = new THREE.PerspectiveCamera(26, 1, 0.1, 100);
    const look = new THREE.Vector3(0, 1.35, 0);

    const sand = track(canvasTexture(512, 512, (g, w, h) => {
      g.fillStyle = '#808080'; g.fillRect(0, 0, w, h);
      for (let i = 0; i < 9000; i++) { const v = Math.random() > 0.5 ? 255 : 0; g.fillStyle = `rgba(${v},${v},${v},.06)`; g.fillRect(Math.random() * w, Math.random() * h, 2, 2); }
    }));
    sand.wrapS = sand.wrapT = THREE.RepeatWrapping; sand.repeat.set(10, 10); sand.colorSpace = THREE.NoColorSpace;
    const ground = new THREE.Mesh(new THREE.PlaneGeometry(60, 60), new THREE.MeshStandardMaterial({ color: 0x5a3322, roughness: 0.95, bumpMap: sand, bumpScale: 1.5 }));
    ground.rotation.x = -Math.PI / 2; ground.receiveShadow = true; scene.add(ground);

    const plinth = new THREE.Mesh(new RoundedBoxGeometry(2.6, 0.36, 2.0, 6, 0.06), new THREE.MeshStandardMaterial({ color: 0x2a1912, roughness: 0.6, metalness: 0.1 }));
    plinth.position.y = 0.18; plinth.castShadow = true; plinth.receiveShadow = true; scene.add(plinth);

    const holes = track(canvasTexture(512, 1024, (g, w, h) => {
      g.fillStyle = '#fff'; g.fillRect(0, 0, w, h); g.fillStyle = '#000';
      for (let y = 40; y < h - 40; y += 30) for (let x = 40; x < w - 30; x += 30) { g.beginPath(); g.arc(x + ((y / 30) % 2 ? 15 : 0), y, 6, 0, 7); g.fill(); }
    }));
    holes.colorSpace = THREE.NoColorSpace;
    const device = new THREE.Group(); scene.add(device);
    const body = new THREE.Mesh(new RoundedBoxGeometry(1.25, 2.5, 1.0, 10, 0.32), new THREE.MeshStandardMaterial({ color: 0xf0e2d0, roughness: 0.62 }));
    body.position.y = 1.61; body.castShadow = true; body.receiveShadow = true; device.add(body);
    const face = new THREE.Mesh(new THREE.PlaneGeometry(0.62, 1.15), new THREE.MeshStandardMaterial({ color: 0xf0e2d0, roughness: 0.65, bumpMap: holes, bumpScale: 5 }));
    face.position.set(0, 1.45, 0.502); device.add(face);
    const ring = new THREE.Mesh(new THREE.TorusGeometry(0.2, 0.035, 24, 64), new THREE.MeshStandardMaterial({ color: 0xd9824a, metalness: 1, roughness: 0.25 }));
    ring.position.set(0, 2.45, 0.505); device.add(ring);
    const btnMat = new THREE.MeshStandardMaterial({ color: 0xff7a3a, emissive: 0xff5a1a, emissiveIntensity: 1.4, roughness: 0.3 });
    const btn = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.16, 0.05, 48), btnMat);
    btn.rotation.x = Math.PI / 2; btn.position.set(0, 2.45, 0.51); device.add(btn);
    const ledMat = new THREE.MeshBasicMaterial({ color: 0xffb27a });
    const led = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.025, 0.01), ledMat);
    led.position.set(0, 0.62, 0.505); device.add(led);

    const puckMat = new THREE.MeshStandardMaterial({ color: 0xeadbc8, roughness: 0.55 });
    [[-1.9, 0.9, 0.22], [2.0, 1.2, 0.16]].forEach(([x, z, r]) => { const p = new THREE.Mesh(new THREE.SphereGeometry(r, 48, 48), puckMat); p.position.set(x, r, z); p.castShadow = true; scene.add(p); });

    const sun = new THREE.DirectionalLight(0xffb27a, 5.5);
    sun.position.set(-7, 2.4, 3); sun.castShadow = true; sun.shadow.mapSize.set(1024, 1024);
    Object.assign(sun.shadow.camera, { left: -6, right: 6, top: 6, bottom: -6, near: 0.5, far: 30 });
    sun.shadow.radius = 4; sun.shadow.bias = -0.0004; scene.add(sun);
    scene.add(new THREE.HemisphereLight(0xffc9a0, 0x2a120a, 0.55));
    const back = new THREE.PointLight(0xff6a2a, 18, 12, 1.6); back.position.set(2.5, 2.2, -3); scene.add(back);
    const glow = new THREE.PointLight(0xff6a2a, 3, 3, 2); glow.position.set(0, 2.45, 1); scene.add(glow);

    const idleBtn = new THREE.Color(0xff5a1a), recBtn = new THREE.Color(0xff2a10);

    return {
      scene, camera, dispose,
      frame(aspect) {
        // المسرح العريض: الجهاز على اليسار والحالة على اليمين
        const wide = aspect > 1.8;
        camera.fov = wide ? 22 : 26;
        camera.position.set(wide ? -1.6 : 3.2, 2.3, wide ? 11.5 : 10.5);
        look.set(wide ? -3.0 : 0, 1.35, 0);
        camera.lookAt(look);
      },
      update(t, dt, s) {
        const rec = s.state === 'Recording', busy = s.state === 'Processing';
        sun.position.set(-7 + Math.sin(t * 0.08) * 1.2, 2.4 + Math.sin(t * 0.05) * 0.3, 3);
        btnMat.emissive.lerp(rec ? recBtn : idleBtn, Math.min(1, dt * 4));
        btnMat.emissiveIntensity = 1.2 + (rec ? 1 + s.smooth * 4 : busy ? Math.sin(t * 6) * 0.8 + 0.8 : Math.sin(t * 1.5) * 0.15);
        glow.intensity = 2.5 + (rec ? 4 + s.smooth * 10 : 0);
        led.scale.x = rec ? 0.15 + s.smooth * 1.6 : busy ? 0.3 + Math.abs(Math.sin(t * 3)) * 0.9 : 1;
        ledMat.color.setHex(rec ? 0xff6a3a : 0xffb27a);
        device.rotation.y = Math.sin(t * 0.25) * 0.06 + (busy ? Math.sin(t * 4) * 0.03 : 0);
      },
    };
  }
}
