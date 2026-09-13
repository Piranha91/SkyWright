"""End-to-end contract for narrated evidence reels: media identity, audio decode, cache, clip cuts and refusal."""
from __future__ import annotations
import hashlib, json, os, shutil, subprocess, sys, tempfile
from pathlib import Path
os.environ["PYTHONUTF8"] = "1"  # Child CLI output and captured text use the same Windows encoding.
HERE=Path(__file__).resolve().parent; HARNESS=HERE.parent; sys.path.insert(0,str(HARNESS)); import evalreel
FAIL=[]
def check(x,m): print(("  PASS  " if x else "  FAIL  ")+m); FAIL.append(m) if not x else None
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def load(p): return json.loads(p.read_text())
def call(p,o,*xs,cwd=None): return subprocess.run([sys.executable,str(HARNESS/"evalreel.py"),"render","--presentation",str(p),"--out",str(o),*xs],capture_output=True,text=True,cwd=cwd)
def frame(ff,p,seconds):
    return subprocess.run([ff,"-v","error","-ss",str(seconds),"-i",str(p),"-frames:v","1","-vf","scale=32:18","-f","rawvideo","-pix_fmt","rgb24","-"],capture_output=True,check=True).stdout
def capture_selection(root):
    source=root/"overlap-emit.json"
    conflicts=[]
    for i,(depth,fraction) in enumerate([(10,.3),(40,.5),(50,.8),(80,.4),(None,None)]):
        conflicts.append({"Kind":"OVERLAP","A":"Earlier.esp","B":"Later.esp",
            "Necessary":[{"FormKey":f"{i+1:06X}:Later.esp","Label":"ExampleCell","Physical":"Holds",
                          "PhysicalDetail":f"the winning boxes overlap {depth}u deep, {fraction} of the smaller"}],
            "Alternative":[{"FormKey":f"{i+1:06X}:Earlier.esp"}]})
    emit={"Probe":"overlapprobe","State":"baseline","Configuration":{"Fingerprint":"fixture-order","Plugins":[{"Name":"Earlier.esp"},{"Name":"Later.esp"}]},"Conflicts":conflicts}
    source.write_text(json.dumps(emit))
    f={"id":"F-overlap","kind":"finding","checkId":"geometry.object-overlap","armId":"baseline","part":"arm","counts":{"A":"Earlier.esp","B":"Later.esp"},"allRows":[{"formKey":c["Necessary"][0]["FormKey"]} for c in conflicts],"evidenceIds":["E-overlap"]}
    p={"findings":[f],"evidence":[{"id":"E-overlap","kind":"emit","part":"arm","armId":"baseline","path":source.name,"sha256":sha(source)}]}
    result=evalreel.capture_requests(p,root); rows=result["requests"]
    check([r["ranking"]["value"] for r in rows]==[40,32,20],"representative overlaps use measured depth/fraction instead of record order")
    check(rows[0]["recordKeys"]==["000003:Later.esp","000003:Earlier.esp"] and [h["color"] for h in rows[0]["highlights"]]==["cyan","magenta"],"overlap capture preserves both participants and assigns distinct colours")
    check(rows[0]["selection"]=={"eligible":5,"selected":3,"omitted":2,"unranked":1},"sampling discloses omitted and unranked candidates")
    check(all(r["camera"]["reportMode"] and r["camera"]["lockPoseAndFramingAcrossViews"] and r["camera"]["pose"] is None for r in rows),"capture requests require clean frames and measured camera planning")
    original=source.read_bytes(); source.write_bytes(original+b" ")
    try: evalreel.capture_requests(p,root); refused=False
    except ValueError as e: refused="changed" in str(e)
    check(refused,"capture selection refuses source evidence changed after planning")
    source.write_bytes(original)
    emit["Probe"]="navownprobe"; emit["Conflicts"]=[{"Kind":"CLOBBERED","A":"Earlier.esp","B":"Later.esp","Necessary":[{"FormKey":"000001:Base.esm","Physical":"Holds","PhysicalDetail":"victim-only ground: 120 sample(s)","Label":"ExampleCell"}]}]
    source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    f.update(checkId="geometry.navmesh-ownership",allRows=[{"formKey":"000001:Base.esm"}],
             counts={"groupKind":"CLOBBERED","A":"Earlier.esp","B":"Later.esp"})
    check(not evalreel.capture_requests(p,root)["requests"],"sampled lost coverage alone cannot select an incompatibility scene")
    emit["Conflicts"][0]["Necessary"][0].update(Physical="Gone",PhysicalDetail="no lost coverage measured")
    support={"Kind":"EDGE-IDX","A":"Later.esp","B":"Patch.esp","Necessary":[
        {"FormKey":"000001:Base.esm","Physical":"Holds","PhysicalDetail":"edge index exceeds winning neighbour count"}],
        "Alternative":[{"FormKey":"000002:Base.esm"}]}
    emit["Conflicts"].append(support)
    source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    check(not evalreel.capture_requests(p,root)["requests"],"a bare overwrite finding cannot borrow an unrelated finding's status")
    f["counts"]={"groupKind":"EDGE-IDX","A":"Later.esp","B":"Patch.esp"}
    nav=evalreel.capture_requests(p,root)["requests"]
    check(len(nav)==1 and [v["navPlugin"] for v in nav[0]["views"]]==["Earlier.esp","Later.esp"],"a measured link mismatch can request paired navmesh history without claiming the overwrite is the problem")
    check(nav[0]["recordKeys"]==["000001:Base.esm","000002:Base.esm"] and nav[0]["corroboration"][0]["kind"]=="EDGE-IDX" and "Patch.esp" in nav[0]["plugins"],
          "navmesh scene names both ends of the actual broken link and retains its evidence")
    support["Necessary"][0]["FormKey"]="000003:Base.esm"
    source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    check(not evalreel.capture_requests(p,root)["requests"],"a mismatch on another record cannot corroborate this overwrite")
    support["Necessary"][0].update(FormKey="000001:Base.esm",Physical="Gone")
    source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    check(not evalreel.capture_requests(p,root)["requests"],"a repaired link cannot justify a remaining incompatibility scene")
    support["Necessary"][0]["Physical"]="Holds"
    source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    p["evidence"][0]["armId"]="other-setup"
    check(not evalreel.capture_requests(p,root)["requests"],"corroboration from another setup cannot justify a scene")
    p["evidence"][0]["armId"]="baseline"
    emit["Configuration"]["Plugins"].reverse(); source.write_text(json.dumps(emit)); p["evidence"][0]["sha256"]=sha(source)
    bad=evalreel.capture_requests(p,root)
    check(not bad["requests"] and bad["limitations"],"navmesh comparison direction refuses a contradictory recorded order")
def media(ff,root):
    d=root/"media"; d.mkdir(); still=d/"viewprobe-clean.png"; clip=d/"game.mp4"; audio=d/"narration.wav"
    subprocess.run([ff,"-y","-f","lavfi","-i","testsrc2=size=1280x720:rate=1","-frames:v","1",str(still)],capture_output=True,check=True)
    subprocess.run([ff,"-y","-f","lavfi","-i","testsrc2=size=640x360:rate=25","-t","6","-c:v","libx264","-pix_fmt","yuv420p",str(clip)],capture_output=True,check=True)
    # Transport fixture only. Working local speech synthesis is tested separately when the host provides it.
    subprocess.run([ff,"-y","-f","lavfi","-i","sine=frequency=440:duration=4",str(audio)],capture_output=True,check=True)
    return still,clip,audio
def plan(root,still,clip,audio):
    verdict="The evidence supports one navmesh regression; two required checks remain unresolved."
    def cap(mid,narr,mode="full-frame"):
        return {"text":"transcript source","narration":narr,"narrationAudio":"media/narration.wav","mediaId":mid,"evidenceIds":["E-1"],"storyboard":{"mode":mode,"cleanFrame":True,"visualReview":{"addedText":"none","subjectVisible":True},"highlights":[{"shape":"rect","x":.3,"y":.3,"width":.2,"height":.2}]}}
    return {"schemaVersion":1,"presentationId":"fixture-present","reader":{"verdict":verdict},"source":{"bundleDir":"."},"settings":{"reel":{"maxSeconds":60}},"media":[
      {"id":"M-I","path":"media/viewprobe-clean.png","sha256":sha(still),"assessed":True,"mediaKind":"offline-render","state":"baseline","subject":{"armId":"baseline"}},
      {"id":"M-V","path":"media/game.mp4","sha256":sha(clip),"assessed":True,"mediaKind":"game-video","state":"candidate","subject":{"armId":"candidate"},"sourceInterval":{"startSeconds":1.0,"endSeconds":4.0}}],"chapters":[
      {"id":"C-01","kind":"opening","captions":[cap("M-I",verdict+" The establishing view locates the tested cell.")]},
      {"id":"C-02","kind":"finding","captions":[cap("M-I","The highlighted navmesh edge is the accepted record-level finding.","crop-pan")]},
      {"id":"C-03","kind":"finding","captions":[cap("M-V","This three-second game clip shows the tested doorway while the explanation continues.")]},
      {"id":"C-04","kind":"closing","captions":[cap("M-I","The remaining checks are unresolved and this view does not prove them.")]}]}
def main():
    found=evalreel.ffmpeg_find(); check(found is not None,"ffmpeg is resolvable")
    if not found:return 1
    ff=found["exe"]; root=Path(tempfile.mkdtemp(prefix="hc-evalreel-test-")); os.environ.setdefault("HOUSECARL_RUNLOG_DIR",str(root/"runlog"))
    try:
      capture_selection(root)
      still,clip,audio=media(ff,root); doc=plan(root,still,clip,audio); pp=root/"presentation.json"; pp.write_text(json.dumps(doc)); out=root/"reel"
      dry=call(pp,out,"--dry-run"); check(dry.returncode==0 and not (out/"reel.mp4").exists(),"dry run plans without rendering")
      r=call(pp,out); check(r.returncode==0,"narrated still/clip reel renders")
      if r.returncode: print(r.stdout,r.stderr); return 1
      st=load(out/"reel-status.json"); mp=load(out/"reel-map.json"); seg=mp["segments"]
      check(st["status"]=="rendered" and st["probe"]["audioDecoded"],"final audio stream independently decodes")
      check((st["probe"]["width"],st["probe"]["height"],st["probe"]["fps"])==(1280,720,30.0),"final video independently probes at 1280x720/30")
      check([s["kind"] for s in seg]==["image","image","clip","image"],"only evidence scenes are emitted; no cards")
      check((seg[2]["sourceInterval"]["startSeconds"],seg[2]["sourceInterval"]["endSeconds"])==(1.0,4.0),"source clip interval is preserved")
      clip_seg=out/seg[2]["file"]
      endframe=frame(ff,clip_seg,3.1); tailframe=frame(ff,clip_seg,4.0)
      check(len(endframe)==1728 and sum(abs(a-b) for a,b in zip(endframe,tailframe))/1728 < 1,"long narration freezes the approved clip end instead of showing later footage")
      check(all(s["audio"]["method"]=="supplied" and s["audio"]["sha256"]==sha(audio) for s in seg),"each scene records supplied-audio identity")
      check(seg[0]["narration"].startswith(doc["reader"]["verdict"]),"opening speaks the shared verdict")
      keys=[s["cacheKey"] for s in seg]; r2=call(pp,"reel",cwd=root); st2=load(out/"reel-status.json"); check(r2.returncode==0 and st2["reused"]==4 and not st2["errors"],"relative output paths reuse cached scenes and produce the review sheet")
      stale=still.read_bytes(); still.write_bytes(stale+b"changed"); rr=call(pp,root/"stale"); check(rr.returncode==2 and "changed since planning" in (rr.stdout+rr.stderr),"changed media refuses rather than substituting a card"); still.write_bytes(stale)
      bad=json.loads(pp.read_text()); bad["chapters"][1]["captions"][0]["storyboard"]={"mode":"full-frame","cleanFrame":False,"visualReview":{"addedText":"none","subjectVisible":False}}; bp=root/"bad.json"; bp.write_text(json.dumps(bad)); br=call(bp,root/"bad"); check(br.returncode==2 and "visualReview" in (br.stdout+br.stderr),"unreviewed framing refuses loudly")
      short=call(pp,root/"short","--chapters","C-02"); check(short.returncode==2 and "opening and closing" in (short.stdout+short.stderr),"chapter selection cannot omit spoken bookends")
      dropped=json.loads(pp.read_text()); dropped["chapters"][-1]["captions"][0].update(omitFromReel=True,omissionReason="test")
      dp=root/"dropped.json"; dp.write_text(json.dumps(dropped)); dr=call(dp,root/"dropped")
      check(dr.returncode==2 and "cannot be omitted" in dr.stdout+dr.stderr,"direct presentation input cannot omit the closing")
      late=json.loads(pp.read_text()); late["chapters"][0]["captions"][0]["narration"]="Welcome. "+late["chapters"][0]["captions"][0]["narration"]
      lp=root/"late.json"; lp.write_text(json.dumps(late)); lr=call(lp,root/"late")
      check(lr.returncode==2 and "start with" in lr.stdout+lr.stderr,"verdict must be the first spoken sentence")
      silent=json.loads(pp.read_text()); silent["chapters"][1]["captions"][0]["narration"]=""
      sp=root/"silent.json"; sp.write_text(json.dumps(silent)); sr=call(sp,root/"silent")
      check(sr.returncode==2 and "no narration" in sr.stdout+sr.stderr,"a silent scene is refused")
      missing_audio=json.loads(pp.read_text()); missing_audio["chapters"][1]["captions"][0]["narrationAudio"]="media/absent.wav"
      ap=root/"absent-audio.json"; ap.write_text(json.dumps(missing_audio)); ar=call(ap,root/"absent-audio")
      check(ar.returncode==2 and "narration-refused" in ar.stdout+ar.stderr and not (root/"absent-audio/reel.mp4").exists(),"missing speech refuses without producing a silent reel")
      packet=root/"storyboard.json"; pr=subprocess.run([sys.executable,str(HARNESS/"evalreel.py"),"storyboard-packet","--presentation",str(pp),"--out",str(packet)],capture_output=True,text=True); check(pr.returncode==0 and len(load(packet)["scenes"])==4,"storyboard review packet covers every scene")
      relocated=root/"moved"; shutil.copytree(root/"media",relocated/"media"); moved=json.loads(pp.read_text()); moved["source"]["bundleDir"]=str(relocated); mpp=root/"moved.json"; mpp.write_text(json.dumps(moved)); mr=call(mpp,root/"moved-out"); check(mr.returncode==0,"bundle relocation preserves media resolution")
      check([s["cacheKey"] for s in load(out/"reel-map.json")["segments"]]==keys,"cache keys remain stable")
      if os.environ.get("HOUSECARL_EVALREEL_KEEP"): shutil.copytree(root,Path(os.environ["HOUSECARL_EVALREEL_KEEP"]),dirs_exist_ok=True)
    finally: shutil.rmtree(root,ignore_errors=True)
    print(f"{len(FAIL)} failure(s)"); return 1 if FAIL else 0
if __name__=="__main__": raise SystemExit(main())
