namespace TokenOverlay;

internal static class AutoCapsuleChecks
{
    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        OverlayPresentation Data(bool longValues) => new(
            new OverlayMetric(DisplayField.Total,"직전","",longValues ? "999999.99M" : "2.68M",true),
            new OverlayMetric(DisplayField.ContextPercent,"⚡* TPS","",longValues ? "123456.78" : "21.36",true),
            [],0,false,null) { CompactMiddle = new OverlayMetric(DisplayField.Thread,"","",longValues ? "9999999.99초" : "890.93초",true) };
        var normal = Data(false); var large = Data(true);
        CodexWindowInfo Host(uint dpi, int width) => new(new IntPtr(1),new IntRect(0,0,width,1200),new IntRect(0,0,width,1200),null,
            new IntRect(0,0,width,1200),dpi,new WindowChromeMetrics(36,24,4,4,4));
        var cases = new List<object>();
        foreach(var dpi in new uint[] {96,120,144,192,216})
        foreach(var scale in new[] {75,100,130})
        {
            var smallSize = AutoCapsule.Measure(normal,dpi,scale);
            var bigSize = AutoCapsule.Measure(large,dpi,scale);
            Check(bigSize.Size.Width > smallSize.Size.Width,$"long values grow capsule at {dpi} DPI / {scale}%");
            var request = new OverlayLayoutRequest(Host(dpi,4000),AnchorMode.InsideTopRight,false,0,false,ScalePercent:scale,Presentation:large);
            var layout = OverlayLayoutCalculator.Calculate(request);
            Check(layout.ScalePercent==scale && layout.CapsuleBounds.Width==bigSize.Size.Width,
                $"configured font scale retained with enough room at {dpi} DPI / {scale}%");
            // Narrower than the capsule at the set scale, wide enough for it at the smallest one (plus the 10-dip side gaps).
            var narrowWidth = (bigSize.Size.Width + AutoCapsule.Measure(large,dpi,ManualAttachmentRules.MinimumScalePercent).Size.Width) / 2
                + 2 * (int)Math.Round(10 * dpi / 96.0);
            var narrow = OverlayLayoutCalculator.Calculate(request with {HostWindow=Host(dpi,narrowWidth)});
            Check(narrow.State!=OverlayVisualState.HiddenForSpace && narrow.CollapsedDisplay==CollapsedDisplayMode.TwoFields
                && narrow.ScalePercent < scale && narrow.WindowBounds.Left >= 0 && narrow.WindowBounds.Right <= narrowWidth,
                $"narrow screen shrinks while retaining all metrics at {dpi} DPI / {scale}%");
            cases.Add(new {dpi,scale,Normal=smallSize.Size.Width,Long=bigSize.Size.Width,NarrowScale=narrow.ScalePercent});
        }
        var expanded = OverlayLayoutCalculator.Calculate(new(Host(96,1920),AnchorMode.InsideTopRight,true,10,false,Presentation:large));
        Check(expanded.State==OverlayVisualState.Expanded && expanded.PanelBounds.Width>=expanded.CapsuleBounds.Width,
            "expanded window contains wider adaptive capsule");
        foreach(var dpi in new uint[]{96,144,216})
        {
            using var form=new TokenStripForm();
            form.SetPresentation(large);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            var layout=OverlayLayoutCalculator.Calculate(new(Host(dpi,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:large));
            form.ApplyLayout(layout);
            using var bitmap=new Bitmap(form.Width,form.Height);
            bitmap.SetResolution(dpi,dpi);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"long-{dpi}.png"));
        }
        // Lower-left note with a count: the count ends under the token count, the top row moves right only if needed.
        foreach(var running in new[]{false,true})
        foreach(var note in new[]{"캐시 만료 109.0k","압축됨 · 새로 저장"})
        {
            var twoLine=normal with {App=HostApp.Claude,Running=running,SubLine="모델 60.65 TPS",SubLineLeft=note,SubLineLeftAlert=true};
            var measured=AutoCapsule.Measure(twoLine,96,100);
            var m=OverlayRenderMetrics.Create(96,100);
            var lead=measured.Icon+measured.Gap+(measured.Dot>0?measured.Dot+m.CompactMetricGap:0);
            using var labelFont=AutoCapsule.LabelFont(96,100);
            using var valueFont=AutoCapsule.ValueFont(96,100);
            if(AutoCapsule.SplitNoteCount(note) is var (label,count))
                Check(measured.CountRight==lead+measured.Shift+measured.Primary
                    && AutoCapsule.Width(label,labelFont)+m.CompactMetricGap+AutoCapsule.Width(count,valueFont)+2<=measured.CountRight
                    && measured.CountRight+measured.Gap+AutoCapsule.SubLineWidth(twoLine.SubLine!,labelFont,valueFont,m.CompactMetricGap)
                        +2*m.HorizontalPadding<=measured.Size.Width,
                    $"note count sits under the token count and clears the model line (running {running})");
            else Check(measured is {CountRight:0,Shift:0},$"a note without a count keeps the top row in place (running {running})");
            using var form=new TokenStripForm();
            form.SetPresentation(twoLine);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(96,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:twoLine)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"note-{(running?"running":"idle")}-{(note.EndsWith('k')?"count":"plain")}.png"));
        }
        // Context ring: a module on top of the collapsed capsule above its right end; the capsule keeps its width.
        // Built the way the live capsule is: a finished Claude turn with its model rate and the cache note.
        try { if (System.Diagnostics.Process.GetProcessesByName("claude").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
            .FirstOrDefault(f => f is not null) is { } claudeExe) AppIconCache.Remember(HostApp.Claude, claudeExe); } catch { }
        TokenSnapshot Live(double? fill) => new("x","x",0,0,0,0,0,0,0,default) { App=HostApp.Claude,
            Turn=new TurnStatistics("t",4_700_800,19_200,0,290,"완료",false,null) { ModelSeconds=251.2, ModelRequests=30 },
            CacheNote="캐시 60분 남음", CacheBar=new CacheBar(1,CacheLevel.Fresh), ContextFill=fill };
        var plainTwo = TurnPresentation.Create(Live(null));
        foreach(var dpi in new uint[]{96,144})
        foreach(var fill in new[]{0.12,0.58,0.86,1.07})
        {
            var ringed=TurnPresentation.Create(Live(fill));
            var request=new OverlayLayoutRequest(Host(dpi,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:ringed);
            var without=OverlayLayoutCalculator.Calculate(request with {Presentation=plainTwo});
            var with=OverlayLayoutCalculator.Calculate(request);
            var rise=with.WindowBounds.Height-without.WindowBounds.Height;
            Check(without.RingBounds.IsEmpty && with.CapsuleBounds.Width==without.CapsuleBounds.Width && rise>0
                && with.WindowBounds.Top==without.WindowBounds.Top && with.WindowBounds.Width==without.WindowBounds.Width
                && with.RingBounds.Top==0 && with.RingBounds.Bottom<with.CapsuleBounds.Top && with.RingBounds.Right==with.CapsuleBounds.Right
                && with.CapsuleBounds.Top-with.RingBounds.Bottom<=OverlayRenderMetrics.Create(dpi,with.ScalePercent).MetricGap,
                $"context ring stands on the capsule's right end, flush with its right edge; at the top right the ring takes the capsule's place and the capsule steps down ({dpi} DPI, {fill:0%})");
            using var form=new TokenStripForm();
            _=form.Handle; // as on screen: the window exists before the layout arrives
            form.SetPresentation(ringed);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(with);
            var ringMid=with.RingBounds.X+with.RingBounds.Width/2;
            using(var probe=Graphics.FromHwnd(IntPtr.Zero))
                Check(form.Region is {} shape && shape.IsVisible(ringMid,with.RingBounds.Bottom-2,probe) && shape.IsVisible(ringMid,with.CapsuleBounds.Top-1,probe)
                    && shape.IsVisible(with.RingBounds.X+1,with.RingBounds.Bottom-1,probe) && shape.IsVisible(ringMid,with.RingBounds.Top+2,probe)
                    && !shape.IsVisible(with.RingBounds.X-3,with.RingBounds.Top+2,probe)
                    && shape.IsVisible(with.CapsuleBounds.Right-1,with.CapsuleBounds.Top+1,probe) && !shape.IsVisible(with.CapsuleBounds.Right,with.RingBounds.Bottom,probe)
                    && form.WindowShapeContains(ringMid,with.RingBounds.Top+2) && form.WindowShapeContains(ringMid,with.RingBounds.Bottom-2)
                    && !form.WindowShapeContains(with.RingBounds.X-3,with.RingBounds.Top+2),
                    $"window shape keeps the whole ring and its foot where it meets the capsule ({dpi} DPI, {fill:0%})");
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            // The window is cut to the capsule and the ring; show only that.
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(form.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,$"ring-{dpi}-{fill*100:0}.png"));
        }
        // A short top row over wider rate lines (42만 8초 82.94 over 생성 534.44 and the cache bar): the capsule widens
        // for the lower lines, and the top row's "TPS" still ends where theirs do (the user's ask, 2026-11-12: "무조건 TPS
        // 글자에 3개 나란히 정렬 되어야지").
        foreach(var dpi in new uint[]{96,144})
        {
            var narrowTop=TurnPresentation.Create(Live(0.27) with { Turn=new TurnStatistics("t",420_000,663,0,8.0,"완료",false,null)
                { ModelSeconds=7.77, ModelRequests=1, PureSeconds=1.24 } });
            var layout=OverlayLayoutCalculator.Calculate(new(Host(dpi,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:narrowTop));
            using var form=new TokenStripForm();
            form.SetPresentation(narrowTop);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(layout);
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"tps-column-{dpi}.png"));
            // Each text line inside the capsule (a run of rows with ink, taller than a divider): where its ink ends.
            var cap=layout.CapsuleBounds;
            bool Ink(int px,int py){var c=bitmap.GetPixel(px,py);return (c.R+c.G+c.B)/3<160;}
            var ends=new List<int>();
            int runTop=-1,runRight=0;
            for(var py=cap.Top+2;py<=cap.Bottom-2;py++)
            {
                var right=-1;
                for(var px=cap.Right-4;px>cap.Left+4;px--) if(Ink(px,py)){right=px;break;}
                if(right>=0){if(runTop<0)runTop=py;runRight=Math.Max(runRight,right);}
                else if(runTop>=0){if(py-runTop>=4)ends.Add(runRight);runTop=-1;runRight=0;}
            }
            Check(ends.Count==3 && ends.Max()-ends.Min()<=1,
                $"top row's TPS ends with the 모델 and 생성 lines' when they are wider ({dpi} DPI; line ends {string.Join(",",ends)})");
        }
        // Draft: the model and its context limit under the ring, the ring raised to make room (drawn for review).
        foreach(var (name,note) in new[]{("ring-note","Opus 5.5\n600K"),("ring-note-long",ContextUsage.Note("claude-sonnet-5-5",1_000_000)!)})
        {
            var noted=TurnPresentation.Create(Live(0.58)) with { RingNote=note };
            var layout=OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:noted));
            var disc=AutoCapsule.RingSize(144,layout.ScalePercent).Disc;
            Check(layout.RingBounds.Bottom<layout.CapsuleBounds.Top && layout.RingBounds.Right==layout.CapsuleBounds.Right
                && layout.RingBounds.Height==disc && layout.RingBounds.Width==disc && layout.RingNoteBounds.Right==layout.RingBounds.Left
                && layout.RingNoteBounds.Bottom==layout.RingBounds.Bottom && layout.RingNoteBounds.Top==layout.RingBounds.Top
                && layout.RingModuleBounds==new IntRect(layout.RingNoteBounds.X,layout.RingBounds.Y,layout.RingNoteBounds.Width+disc,disc)
                && layout.WindowBounds.Height==OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,
                    Presentation:noted with { RingNote=null })).WindowBounds.Height
                && OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,
                    Presentation:noted with { ContextFill=null })) is { RingModuleBounds.IsEmpty: true, RingNoteBounds.IsEmpty: true },
                $"ring note: one module with the ring, left of it and as tall as it, the ring and the window as without it; gone with the ring ({name})");
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(noted);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(layout);
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(form.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // The ring's module with the divider where it meets the capsule, in both themes (drawn for review).
        foreach(var kind in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
        {
            var noted=TurnPresentation.Create(Live(0.58)) with { RingNote="Opus 5.5\n600K" };
            var layout=OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:noted));
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(noted);
            form.ApplyTheme(OverlayThemePalette.For(kind));
            form.ApplyLayout(layout);
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(form.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,$"ring-divider-{(kind==OverlayThemeKind.Light?"light":"dark")}.png"));
        }
        // The CPU meter (a setting): Task Manager's tray icon at the left end of the ring's row, which then runs the
        // capsule's full width, so the overlay is one rounded rectangle (drawn for review).
        // A graphics card reading (fan percent, temperature).
        static GpuReading Gpu(int fan,int temp)=>new(fan,temp);
        static string? PythonOnPath()=>(Environment.GetEnvironmentVariable("PATH")??"").Split(';',StringSplitOptions.RemoveEmptyEntries)
            .Select(d=>Path.Combine(d.Trim(),"python.exe")).FirstOrDefault(p=>File.Exists(p) && !p.Contains("WindowsApps",StringComparison.OrdinalIgnoreCase));
        Bitmap Shot(TokenStripForm f)
        {
            var shot=new Bitmap(f.Width,f.Height);
            f.DrawToBitmap(shot,new Rectangle(0,0,f.Width,f.Height));
            using(var g=Graphics.FromImage(shot))
            for(var py=0;py<shot.Height;py++)
            for(var px=0;px<shot.Width;px++)
                if(f.Region is {} cut && !cut.IsVisible(px,py,g)) shot.SetPixel(px,py,Color.Transparent);
            return shot;
        }
        foreach(var dpi in new uint[]{96,144})
        {
            var noted=TurnPresentation.Create(Live(0.58)) with { RingNote="Opus 5.5\n600K" };
            var request=new OverlayLayoutRequest(Host(dpi,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:noted);
            var off=OverlayLayoutCalculator.Calculate(request);
            var on=OverlayLayoutCalculator.Calculate(request with {Presentation=noted with {CpuMeter=true}});
            var bar=on.CpuBarBounds;
            var m=OverlayRenderMetrics.Create(dpi,on.ScalePercent);
            var gap=AutoCapsule.RingSize(dpi,on.ScalePercent).Margin;
            // The CPU line over the meters' row (2026-11-09): the row and the capsule move down by it; at the host's top
            // right the window keeps its top (the host is taken as lower by the line too) and grows down.
            var line=on.TopLineBounds.Height;
            IntRect Down(IntRect r)=>r with {Y=r.Y+line};
            Check(off.TopLineBounds.IsEmpty && line>0 && on.TopLineBounds==new IntRect(on.CapsuleBounds.X,off.RingBounds.Y,on.CapsuleBounds.Width,line)
                && on.TopLineBounds.Bottom==on.RingModuleBounds.Top && line<on.RingBounds.Height/2
                && TokenStripForm.CapsuleWithRow(on).Top==on.TopLineBounds.Top,
                $"CPU line: a thin line over the meters' row, under half the ring's height, inside the one rectangle ({dpi} DPI; {on.TopLineBounds})");
            Check(!off.FullTopRow && off.CpuBarBounds.IsEmpty && on.FullTopRow
                && on.RingModuleBounds==new IntRect(on.CapsuleBounds.X,on.RingBounds.Y,on.CapsuleBounds.Width,on.RingBounds.Height)
                && on.RingBounds==Down(off.RingBounds) && on.RingNoteBounds==Down(off.RingNoteBounds) && on.CapsuleBounds==Down(off.CapsuleBounds)
                && on.WindowBounds==off.WindowBounds with {Height=off.WindowBounds.Height+line}
                && bar.Width*8<=bar.Height*7+8 && bar.Width*2>=bar.Height && bar.Left>on.CapsuleBounds.Left && bar.Top>on.RingBounds.Top && bar.Bottom<on.RingBounds.Bottom
                && on.RamBarBounds==bar with {X=bar.Right+gap} && on.PageBarBounds==bar with {X=on.RamBarBounds.Right+gap}
                && on.PageBarBounds.Right<on.RingNoteBounds.Left,
                $"Meters: the row runs the capsule's width, the ring, its lines, the capsule and the window stay; CPU, memory and page file bars at most 7 wide to 8 tall, a margin apart ({dpi} DPI; {bar} {on.RamBarBounds} {on.PageBarBounds} {on.RingNoteBounds} {on.CapsuleBounds})");
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(noted with {CpuMeter=true});
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(on);
            using(var probe=Graphics.FromHwnd(IntPtr.Zero))
                Check(form.Region is {} shape && shape.IsVisible(on.CapsuleBounds.X+1,on.RingBounds.Top+m.CapsuleRadius+1,probe)
                    && shape.IsVisible(bar.Left,bar.Top,probe) && shape.IsVisible(on.CapsuleBounds.X+on.CapsuleBounds.Width/3,on.RingBounds.Top+1,probe)
                    && !shape.IsVisible(on.CapsuleBounds.X,on.TopLineBounds.Top,probe) && !shape.IsVisible(on.CapsuleBounds.Right-1,on.CapsuleBounds.Bottom-1,probe)
                    && form.WindowShapeContains(on.CapsuleBounds.X+1,on.RingBounds.Top+m.CapsuleRadius+1),
                    $"CPU meter: the window is one rounded rectangle over the row and the capsule ({dpi} DPI)");
            if(dpi!=144) continue;
            foreach(var kind in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
            foreach(var (cpu,ram,page) in new (double?,double?,GpuReading?)[]{(0.07,0.84,Gpu(30,35)),(0.43,0.62,Gpu(55,62)),(0.87,0.95,Gpu(100,87)),(null,null,null)})
            {
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetMeters(cpu,ram,page);
                using var shot=Shot(form);
                shot.Save(Path.Combine(directory,$"cpu-{(kind==OverlayThemeKind.Light?"light":"dark")}-{(cpu is double c?$"{c*100:0}":"none")}.png"));
            }
            // Each whole percent shows: 42% and 43% differ inside the bar.
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.SetMeters(0.42,0.5,Gpu(30,35));
            using var at42=Shot(form);
            form.SetMeters(0.43,0.5,Gpu(30,35));
            using var at43=Shot(form);
            var differs=false;
            for(var py=bar.Top;py<bar.Bottom && !differs;py++)
                // Near the bar's left edge, clear of the number in its middle.
                if(at42.GetPixel(bar.Left+bar.Width/8+2,py)!=at43.GetPixel(bar.Left+bar.Width/8+2,py)) differs=true;
            Check(differs && form.CpuFill==0.43,"CPU meter: one percent more fills a little more of the bar");
            // Draft pictures of the CPU line (2026-11-09), like the user's screen: ring 25%, Opus 5.5 500K.
            var drafted=TurnPresentation.Create(Live(0.25)) with { RingNote="Opus 5.5\n500K", CpuMeter=true };
            var draftLayout=OverlayLayoutCalculator.Calculate(request with {Presentation=drafted});
            form.SetPresentation(drafted);
            form.ApplyLayout(draftLayout);
            foreach(var kind in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
            // The program and its share first, so a long session name is what "…" cuts.
            // No "CPU 1위" ahead of it: the user knows what the line is (2026-11-09).
            // Python by its logo, from a python.exe on this PC's PATH (2026-11-10); "python-name" is the same line without it.
            foreach(var (name,text,logo) in new[]{("claude","python 10% · 토큰 오버레이",(string?)null),("app","KakaoTalk 3%",null),
                ("long","dotnet 37% · B2 그룹 묶기·기본 타깃 리버싱 · 새 F 계산 조건",null),("waiting",(string?)null,null),
                ("python","python 45% · C 판정 — 실측 기준·판정 도구",PythonOnPath()),("python-name","python 45% · C 판정 — 실측 기준·판정 도구",null)})
            {
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetMeters(1.0,0.72,Gpu(30,35));
                form.SetTopLine(text,logo);
                using var shot=Shot(form);
                shot.Save(Path.Combine(directory,$"topline-{(kind==OverlayThemeKind.Light?"light":"dark")}-{name}.png"));
            }
            form.SetTopLine(null);
            Check(CpuTop.HasLogo("python") && CpuTop.HasLogo("pythonw") && CpuTop.HasLogo("Python3.14") && !CpuTop.HasLogo("py")
                && !CpuTop.HasLogo("pythonista") && !CpuTop.HasLogo("dotnet"),"CPU line: python, pythonw, python3.14 go by the logo; other programs by name");
            var python=PythonOnPath();
            if(python is null) Console.WriteLine("SKIP CPU line logo: no python.exe on PATH");
            else
            {
                Bitmap Line(string? logo){ form.SetTopLine("python 45% · 세션",logo); return Shot(form); }
                using var named=Line(null);
                using var logoed=Line(python);
                using var wrong=Line(Path.Combine(directory,"no-such-file.exe"));
                var area=draftLayout.TopLineBounds.ToRectangle();
                bool Same(Bitmap a,Bitmap b){ for(var y=area.Top;y<area.Bottom;y++) for(var x=area.Left;x<area.Right;x++) if(a.GetPixel(x,y)!=b.GetPixel(x,y)) return false; return true; }
                Check(!Same(named,logoed),"CPU line: python's own icon replaces its name");
                Check(Same(named,wrong),"CPU line: a file without an icon leaves the name");
                form.SetTopLine(null);
            }
        }
        // The meter alone (context ring off or not known yet): the row with the meter only, still one rectangle.
        {
            var alone=plainTwo with {CpuMeter=true};
            var layout=OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:alone));
            Check(layout.FullTopRow && layout.RingBounds.IsEmpty && layout.RingNoteBounds.IsEmpty && !layout.PageBarBounds.IsEmpty
                && OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:alone with {StatusText="확인 중…"})).CpuBarBounds.IsEmpty
                && OverlayLayoutCalculator.Calculate(new(Host(144,1920),AnchorMode.InsideTopRight,true,10,false,Presentation:alone)).CpuBarBounds.IsEmpty,
                "CPU meter: shown alone when there is no ring; not on a waiting capsule or the open panel");
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(alone);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
            form.ApplyLayout(layout);
            form.SetMeters(0.31,0.84,Gpu(30,35));
            using var shot=Shot(form);
            shot.Save(Path.Combine(directory,"cpu-alone-dark.png"));
        }
        // The CPU line's reading: groups by program, a process under a session's claude.exe counted for the session.
        {
            const string sid="1a2b3c4d-0000-4000-8000-000000000001";
            CpuTop.Proc P(int pid,int parent,string exe,long used,string? session=null,long created=10)=>new(pid,parent,exe,created,used,session);
            CpuTop.Proc[] Tree(long python,long chrome,long node)=>[
                P(100,4,"explorer.exe",1),
                P(200,100,"chrome.exe",chrome/4),P(201,200,"chrome.exe",chrome-chrome/4),
                P(300,100,"claude.exe",5),P(310,300,"claude.exe",0,sid),P(320,310,"cmd.exe",0),P(330,320,"python.exe",python),
                P(340,300,"claude.exe",1,""),P(350,340,"node.exe",node)];
            string? Title(string id)=>id==sid?"토큰 오버레이":null;
            var reused=new[]{P(100,4,"explorer.exe",0),P(500,100,"game.exe",5,created:50),P(600,500,"helper.exe",70,created:20)};
            // Seen 2026-11-09: python workers under `timeout`, whose shell had exited.
            var orphaned=new[]{P(700,8456,"timeout.exe",0),P(701,700,"timeout.exe",0),P(702,701,"python.exe",2),P(703,702,"python.exe",30),P(704,702,"python.exe",31)};
            Check(CpuTop.Line(Tree(50,40,20),1000,Title)=="python 5% · 토큰 오버레이"
                && CpuTop.Line(Tree(20,40,10),1000,Title)=="chrome 4%"
                && CpuTop.Line(Tree(20,40,60),1000,Title)=="node 6% · "+CpuTop.UnknownSession
                && CpuTop.Line(Tree(50,40,20),1000,_=>null)=="python 5% · 1a2b3c4d"
                && CpuTop.Line(reused,1000,Title)=="helper 7%" && CpuTop.Line(orphaned,1000,Title)=="python 6%"
                && CpuTop.Line(Tree(0,0,0).Select(p=>p with {Used=-1}).ToArray(),1000,Title) is null && CpuTop.Line(Tree(50,40,20),0,Title) is null
                && CpuTop.SessionOf($"\"C:\\x\\claude.exe\" --output-format stream-json --resume {sid} --verbose")==sid
                && CpuTop.SessionOf("claude.exe --output-format stream-json --await-claim")==""
                && CpuTop.SessionOf("claude.exe --type=renderer --user-data-dir=x") is null,
                "CPU line: the heaviest program group, a session's own processes named by the session (or 이름 모름), a reused pid not taken for a parent");
            var reports=Directory.CreateTempSubdirectory("cputop-");
            try
            {
                File.WriteAllText(Path.Combine(reports.FullName,sid+".json"),$"{{\"v\":1,\"code\":9,\"sessionId\":\"{sid}\",\"pid\":340}}");
                var stale=Path.Combine(reports.FullName,"aaaaaaaa-0000-0000-0000-000000000000.json");
                File.WriteAllText(stale,"{\"v\":1,\"code\":9,\"sessionId\":\"aaaaaaaa-0000-0000-0000-000000000000\",\"pid\":350}");
                File.SetLastWriteTimeUtc(stale,DateTime.UtcNow.AddMinutes(-5));
                File.WriteAllText(Path.Combine(reports.FullName,"bbbbbbbb-0000-0000-0000-000000000000.json"),"{\"v\":1,\"code\":8,\"sessionId\":\"bbbbbbbb-0000-0000-0000-000000000000\"}");
                var pids=CpuTop.ReadSessionPids(reports.FullName,DateTimeOffset.Now);
                Check(pids.Count==1 && pids[340]==sid,"CPU line: a fresh session's process is named by its mod's report (build 9), a stale or older report is not");
            }
            finally { reports.Delete(true); }
            // Orphaned work (2026-11-10): its head is found, and its environment's session id names it.
            static byte[] Block(params string[] vars)=>System.Text.Encoding.Unicode.GetBytes(string.Concat(vars.Select(v=>v+"\0"))+"\0");
            var heads=CpuTop.Orphans(orphaned);
            var named=orphaned.Select(p=>p.Pid==700?p with {Session=sid}:p).ToArray();
            Check(heads.Count==1 && heads[700]==704 && CpuTop.Orphans(Tree(50,40,20)).Count==0
                && CpuTop.Line(named,1000,Title)=="python 6% · 토큰 오버레이"
                && CpuTop.FindSession(Block("PATH=C:\\x","CLAUDE_CODE_SESSION_ID="+sid,"OTHER=1"))==sid
                && CpuTop.FindSession(Block("CLAUDE_CODE_SESSION_ID="+sid))==sid
                && CpuTop.FindSession(Block("XCLAUDE_CODE_SESSION_ID="+sid))==null
                && CpuTop.FindSession(Block("CLAUDE_CODE_SESSION_ID=not-an-id-at-all-but-36-chars-long!!"))==null
                && CpuTop.FindSession(Block("PATH=C:\\x"))==null,
                "CPU line: work whose shell has exited is named by the session id in its environment, that variable alone");
            System.Diagnostics.Process? Child(string? session)
            {
                var start=new System.Diagnostics.ProcessStartInfo("cmd.exe","/c ping -n 6 127.0.0.1 >nul"){UseShellExecute=false,CreateNoWindow=true};
                start.Environment.Remove("CLAUDE_CODE_SESSION_ID");
                if(session is not null) start.Environment["CLAUDE_CODE_SESSION_ID"]=session;
                return System.Diagnostics.Process.Start(start);
            }
            using(var marked=Child(sid))
            using(var plain=Child(null))
            {
                var fromMarked=CpuTop.SessionFromEnvironment(marked!.Id);
                var fromPlain=CpuTop.SessionFromEnvironment(plain!.Id);
                try { marked.Kill(); plain.Kill(); } catch(InvalidOperationException) { }
                Check(fromMarked==sid && fromPlain is null,$"CPU line: a live process's environment gives its session id ({fromMarked is not null}, {fromPlain is null})");
            }
            using var live=new CpuTop(start:false);
            var first=live.Sample();
            var spin=System.Diagnostics.Stopwatch.StartNew(); while(spin.ElapsedMilliseconds<300) { }
            var second=live.Sample();
            Check(first is null && second is { } read && System.Text.RegularExpressions.Regex.IsMatch(read,@"^\S.* \d+%( · .+)?$"),
                $"CPU line: a live reading from Windows ({second})");
        }
        Check(CpuUsage.Between((100,1000),(150,1100))==0.5 && CpuUsage.Between((100,1000),(100,1000)) is null
            && CpuUsage.Between((0,0),(200,200))==0 && TokenStripForm.CpuPercent(0.426)==43 && TokenStripForm.CpuPercent(1.3)==100 && TokenStripForm.CpuPercent(null) is null,
            "CPU meter: the share from Windows' time totals, shown in whole percents");
        using(var live=new CpuUsage()) { live.Sample(); System.Threading.Thread.Sleep(300); Check(live.Sample() is >= 0 and <= 1,"CPU meter: a live reading from Windows");
            Check(CpuUsage.Memory() is > 0 and <= 1,"Meters: a live memory reading from Windows"); }
        // The third bar (2026-11-10): the graphics card's fan speed fills it, its temperature, whole, at the foot.
        Check(GpuReading.Label(Gpu(30,35))=="35°" && GpuReading.Label(null)=="—" && GpuReading.Label(new GpuReading(40,null))=="—"
            && Gpu(30,35).Fill==0.3 && Gpu(120,80).Fill==1 && new GpuReading(null,50).Fill is null,
            "GPU meter: fan speed fills the bar (stopping at full), the temperature whole at the foot");
        using(var gpu=new GpuSensor())
        {
            var started=System.Diagnostics.Stopwatch.StartNew();
            var reading=gpu.Read();
            var first=started.Elapsed.TotalMilliseconds;
            started.Restart();
            var again=gpu.Read();
            var next=started.Elapsed.TotalMilliseconds;
            Check(reading is null || (reading.Value.Temp is > 0 and < 120 && reading.Value.Fan is null or >= 0 && again is not null),
                $"GPU meter: a live reading from the card's driver, or none without NVIDIA ({(reading is {} r?$"fan {r.Fan}% {r.Temp}°C":"none")}; first {first:0} ms, next {next:0.0} ms)");
        }
        // The ring switched off in the settings: the capsule as before the ring (drawn for review).
        using(var offForm=new TokenStripForm())
        {
            _=offForm.Handle;
            offForm.SetPresentation(plainTwo);
            offForm.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            offForm.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:plainTwo)));
            using var bitmap=new Bitmap(offForm.Width,offForm.Height);
            offForm.DrawToBitmap(bitmap,new Rectangle(0,0,offForm.Width,offForm.Height));
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(offForm.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,"ring-144-off.png"));
        }
        // Compacted chat: minutes left while its cache lives, the lapse first once it lapsed, the ring kept (drawn for review).
        var compactedAt=DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        var compactedState=new CacheState(301_010,300_000,compactedAt,60,0,Compacted:true);
        foreach(var (name,minutes) in new[]{("compact-alive",14.0),("compact-lapsed",61.0),("compact-before",61.0),("plain-lapsed",61.0)})
        {
            var now=compactedAt.AddMinutes(minutes); var st=name=="plain-lapsed" ? compactedState with { Compacted=false, LastContext=109_000 } : compactedState;
            // Right after a compaction the ring shows the summary plus the chat's learned overhead (about 18% of 400k).
            var snapshot=Live(name=="plain-lapsed" ? 109_000/400_000.0 : 0.18) with { CacheNote=name=="compact-before" ? "압축됨 · 새로 저장" : CacheStatus.CapsuleNote(st,now),
                CacheBar=name=="compact-before" ? null : CacheStatus.CapsuleBar(st,now), CacheAlert=CacheStatus.CapsuleAlert(st,now) && name!="compact-before" };
            var shown=TurnPresentation.Create(snapshot);
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(shown);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:shown)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(form.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // Codex: the last request's hit share where Claude counts down, no bar (drawn for review).
        foreach(var (name,cached) in new[]{("codex-hit",40_715L),("codex-miss",0L),("codex-before",-1L)})
        {
            var st=new CacheState(4_700_800,4_200_000,compactedAt,null,41_082,HitOnly:true,LastCached:cached);
            // codex-before: the countdown this replaces, 20 of 30 minutes left.
            var shown=TurnPresentation.Create(Live(0.18) with { App=HostApp.Codex,
                CacheNote=cached<0 ? "캐시 20분 남음" : CacheStatus.CapsuleNote(st,compactedAt),
                CacheBar=cached<0 ? new CacheBar(20/30.0,CacheLevel.Fresh) : CacheStatus.CapsuleBar(st,compactedAt), CacheAlert=CacheStatus.CapsuleAlert(st,compactedAt) });
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(shown);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:shown)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            using(var g=Graphics.FromImage(bitmap))
            for(var py=0;py<bitmap.Height;py++)
            for(var px=0;px<bitmap.Width;px++)
                if(form.Region is {} cut && !cut.IsVisible(px,py,g)) bitmap.SetPixel(px,py,Color.Transparent);
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // The open list with the weekly line under the device names (drawn for review).
        foreach(var (name,names) in new[]{("weekly-named",new WeeklySplit.Names("PC","랩탑")),("weekly-default",(WeeklySplit.Names?)null)})
        {
            var line=WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(),null,38,31,0,ThisPcOnly:22),names);
            var listed=plainTwo with { History=[new HistoryRow("1턴 전","472만","4분 11초","19.2k","60.65")],
                HistoryFooter=line+"\n"+WeeklyUsageMath.ResetLine(DateTimeOffset.Parse("2026-10-24T11:00:00Z").ToUnixTimeMilliseconds(),compactedAt) };
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(listed);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.SetShowHistory(true);
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                Presentation:listed,CompactPanel:true)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // Draft: someone else sharing the folder gets their own part of the weekly bar, in both themes.
        foreach(var (name,kind) in new[]{("weekly-person-light",OverlayThemeKind.Light),("weekly-person-dark",OverlayThemeKind.Dark)})
        {
            var line=WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(),null,38,19,0,ThisPcOnly:12,People:[14]),
                new WeeklySplit.Names("PC","랩탑"),["친구1"]);
            var listed=plainTwo with { History=[new HistoryRow("1턴 전","472만","4분 11초","19.2k","60.65")],
                HistoryFooter=line+"\n"+WeeklyUsageMath.ResetLine(DateTimeOffset.Parse("2026-10-24T11:00:00Z").ToUnixTimeMilliseconds(),compactedAt) };
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(listed);
            form.ApplyTheme(OverlayThemePalette.For(kind));
            form.SetShowHistory(true);
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                Presentation:listed,CompactPanel:true)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // Draft: every one of the user's devices by name instead of "내 다른 기기" for two or more.
        foreach(var (name,week) in new[]{
            ("weekly-devices-before","이번주 17(토)~ · PC 35% · 내 다른 기기 0% · 다른 곳 0% · 계정 35%"),
            ("weekly-devices-now","이번주 17(토)~ · PC 35% · LAPTOP-1 0% · 랩탑 0% · 다른 곳 0% · 계정 35%"),
            ("weekly-devices-used","이번주 17(토)~ · PC 26% · LAPTOP-1 6% · 랩탑 3% · 다른 곳 0% · 계정 35%")})
        {
            var listed=plainTwo with { History=[
                    new HistoryRow("1턴 전","358.3k","39초","76.44","80.32","Opus 5.5","99%"),
                    new HistoryRow("2턴 전","116.4k","4초","55.05","55.19","Opus 5.5","100%"),
                    new HistoryRow("3턴 전","345.4k","20초","78.14","78.48","Opus 5.5","100%")],
                HistoryFooter="캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n"+week+"\n초기화 10-24 (토) 20:00 · 1시간 53분 남음" };
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(listed);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.SetShowHistory(true);
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                Presentation:listed,CompactPanel:true)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,$"{name}.png"));
        }
        // Tokens spent (user, 2026-11-11): whole 만 below 1억, 억 with one decimal from there, rounded half away from zero.
        Check(OverlayPresentationBuilder.FormatTokenCount(72_000_000)=="7200만" && OverlayPresentationBuilder.FormatTokenCount(48_263_448)=="4826만"
            && OverlayPresentationBuilder.FormatTokenCount(150_000_000)=="1.5억" && OverlayPresentationBuilder.FormatTokenCount(2_071_100)=="207만"
            && OverlayPresentationBuilder.FormatTokenCount(15_000)=="2만" && OverlayPresentationBuilder.FormatTokenCount(11_040_170)=="1104만"
            && OverlayPresentationBuilder.FormatTokenCount(99_994_999)=="9999만" && OverlayPresentationBuilder.FormatTokenCount(99_995_000)=="1.0억"
            && OverlayPresentationBuilder.FormatTokenCount(100_000_000)=="1.0억" && OverlayPresentationBuilder.FormatTokenCount(1_049_999_999)=="10.5억"
            && OverlayPresentationBuilder.FormatTokenCount(125_000_000)=="1.3억" && OverlayPresentationBuilder.FormatTokenCount(4_800)=="4,800"
            && OverlayPresentationBuilder.FormatContextCount(109_000)=="109.0k",
            "tokens spent read in 만 below 1억 and in 억 with one decimal from there; a context's size stays in k and M");
        // Draft: the capsule and the open list with 억, at this PC's measured turn sizes (p25, p50, p90 of 2,904 turns) and two large ones.
        foreach(var (name,kind) in new[]{("eok-dark",OverlayThemeKind.Dark),("eok-light",OverlayThemeKind.Light)})
        {
            TokenSnapshot Spent(long total, string note) => new("x","x",0,0,0,0,0,0,0,default) { App=HostApp.Claude,
                Turn=new TurnStatistics("t",total,19_200,0,290,"완료",false,null) { ModelSeconds=251.2, ModelRequests=30 },
                CacheNote=note, CacheBar=new CacheBar(1,CacheLevel.Fresh) };
            var capsules=new[]{(2_071_100L,"캐시 60분 남음"),(48_263_448L,"캐시 만료 109.0k"),(150_000_000L,"캐시 60분 남음")};
            for(var i=0;i<capsules.Length;i++)
            {
                var p=TurnPresentation.Create(Spent(capsules[i].Item1,capsules[i].Item2));
                using var form=new TokenStripForm();
                form.SetPresentation(p);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,false,0,false,Presentation:p)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"{name}-capsule{i}.png"));
            }
            {
                string F(long n)=>OverlayPresentationBuilder.FormatTokenCount(n);
                // Claude in front, so the Ctrl list also holds the cache sessions under the turns (user, 2026-11-11).
                var listed=plainTwo with { Sessions=[
                        new("00000000-0000-0000-0000-000000000001","오버레이 캐시 알림","7분 남음 · 351K",true,"18:12 자동 압축 예정",true,false,true,true),
                        new("00000000-0000-0000-0000-000000000002","블로그 글 퇴고","24분 남음 · 212K",false,"17:21 자동 핑함 · 다음 18:11 · 유지 ~01:21",true,true,true,true),
                        new("00000000-0000-0000-0000-000000000003","dsh-worker","41분 남음 · 168K",false,"핑 보내는 중…",false,false,true,true)],
                    History=[
                        new HistoryRow("1턴 전",F(949_588),"39초","76.44","80.32","Opus 5.5","99%"),
                        new HistoryRow("2턴 전",F(2_071_100),"1분 4초","55.05","55.19","Opus 5.5","100%"),
                        new HistoryRow("3턴 전",F(11_040_170),"4분 20초","78.14","78.48","Opus 5.5","100%"),
                        new HistoryRow("4턴 전",F(72_000_000),"21분 3초","61.20","63.02","Opus 5.5","100%"),
                        new HistoryRow("5턴 전",F(150_000_000),"48분 10초","58.71","60.10","Opus 5.5","100%")],
                    HistoryFooter="캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n이번주 17(토)~ · PC 35% · 다른 곳 0% · 계정 35%\n초기화 10-24 (토) 20:00 · 1시간 53분 남음" };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"{name}-list.png"));
            }
            {
                // Codex in front (user, 2026-11-11 screenshot): "2940만" and "2시간 11분" were cut off in the narrow list.
                var listed=plainTwo with { History=[
                        new HistoryRow("1턴 전","2940만","2시간 11분","14.01","22.19","6 Astra","99%"),
                        new HistoryRow("2턴 전","1388만","36분 40초","22.76","23.90","6 Astra","99%"),
                        new HistoryRow("3턴 전","1250만","26분 14초","22.80","—","6 Astra","95%"),
                        new HistoryRow("4턴 전","186만","8분 36초","27.61","—","6 Astra","92%")],
                    HistoryFooter="마지막 요청 캐시히트 99.0% · 세션 캐시 읽기 98%\n이번주 6(금)~ · 나 78% · 다른 곳 22% · 계정 100%\n초기화 11-13 (금) 17:22 · 1일 23시간 남음" };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"{name}-codex-list.png"));
            }
        }
        // A list column grows when its text would not fit its share, and only then (user, 2026-11-11: cut-off "2940만").
        {
            HistoryRow[] wide=[new("1턴 전","9999만","12시간 59분","14.01","22.19","6 Astra","100%")];
            HistoryRow[] narrow=[new("1턴 전","95만","39초","76.44","80.32","Opus 5.5","99%")];
            var grown=TokenStripForm.HistoryColumnGrowth(plainTwo with { History=wide },144,100);
            var kept=TokenStripForm.HistoryColumnGrowth(plainTwo with { History=narrow },144,100);
            Check(grown[1]>0 && grown[2]>0 && kept.All(g=>g==0)
                && TokenStripForm.HistoryFixedWidth(plainTwo with { History=wide },144,100)
                    ==TokenStripForm.HistoryFixedWidth(plainTwo with { History=narrow },144,100)+grown.Sum(),
                "a list column widens to fit 9999만 and 12시간 59분, and a list whose text fits keeps its width");
        }
        // Claude in front: the Ctrl list holds the cache sessions with a 핑 button each, in both themes and empty.
        {
            SessionRow[] rows=[
                new("00000000-0000-0000-0000-000000000001","오버레이 캐시 알림","7분 남음 · 351K",true,"18:12 자동 압축 예정",true,false,true,true),
                new("00000000-0000-0000-0000-000000000002","블로그 글 퇴고","24분 남음 · 212K",false,"17:21 자동 핑함 · 다음 18:11 · 유지 ~01:21",true,true,true,true),
                new("00000000-0000-0000-0000-000000000006","보고서 정리","33분 남음 · 287K",false,CachePings.ArmText,true,false,true,true,true),
                new("00000000-0000-0000-0000-000000000007","코드 리뷰","38분 남음 · 176K",false,"압축 요청함…",true,false,true,false),
                new("00000000-0000-0000-0000-000000000003","dsh-worker","41분 남음 · 168K",false,"핑 보내는 중…",false,false,true,true),
                new("00000000-0000-0000-0000-000000000005","어제 연 세션","45분 남음 · 240K",false,CachePings.UpdateText,true,false,false),
                new("00000000-0000-0000-0000-000000000004","터미널 세션","48분 남음 · 190K",false,"확장 없음",false)];
            var footer="캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n이번주 17(토)~ · PC 26% · LAPTOP-1 6% · 랩탑 3% · 다른 곳 0% · 계정 35%\n초기화 10-31 (토) 20:00 · 6일 2시간 남음";
            var claude=TurnPresentation.Create(Live(0.58));
            foreach(var (name,kind,shown) in new[]{("cache-list-dark",OverlayThemeKind.Dark,rows),("cache-list-light",OverlayThemeKind.Light,rows),
                ("cache-list-empty",OverlayThemeKind.Dark,Array.Empty<SessionRow>())})
            {
                var listed=claude with { Sessions=shown, HistoryFooter=footer, History=[new HistoryRow("1턴 전","472만","4분 11초","19.2k","60.65")] };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"{name}.png"));
                var buttons=form.SessionButtons();
                var layout=form.CurrentLayout!;
                // The one recent turn sits on top (its column header and row), then the sessions' title row (user, 2026-11-11).
                var sessionsTop=layout.PanelBounds.ToRectangle().Bottom-OverlayRenderMetrics.Create(layout.Dpi,layout.ScalePercent).PanelPadding
                    -layout.ExpandedRowHeight*(listed.HistoryPanelRows-3);
                Check(listed.SessionsTopRow==3 && listed.HistoryPanelRows==3+Math.Max(1,shown.Length)+5 && buttons.Count==3*shown.Length
                    && buttons.All(b=>layout.PanelBounds.ToRectangle().Contains(b.Bounds) && b.Bounds.Top>=sessionsTop)
                    && buttons.Chunk(3).All(r=>r[0].Kind==TokenStripForm.SessionButton.Compact && r[1].Kind==TokenStripForm.SessionButton.Keep
                        && r[2].Kind==TokenStripForm.SessionButton.Ping && r[0].Bounds.Right<r[1].Bounds.Left && r[1].Bounds.Right<r[2].Bounds.Left),
                    $"cache list ({name}): one row per session (or the line saying none) above the footer, 압축, 유지 then 핑 inside the panel");
                var pinged=new List<string>();
                var kept=new List<string>();
                form.PingClicked+=(_,id)=>pinged.Add(id);
                form.KeepClicked+=(_,id)=>kept.Add(id);
                var compacted=new List<string>();
                form.CompactClicked+=(_,id)=>compacted.Add(id);
                var capsuleClicks=0;
                form.CapsuleClicked+=(_,_)=>capsuleClicks++;
                foreach(var (bounds,_,_) in buttons) form.SimulateClick(new Point(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2));
                Check(pinged.SequenceEqual(shown.Where(r=>r.CanPing).Select(r=>r.Id)) && kept.SequenceEqual(shown.Where(r=>r.CanKeep).Select(r=>r.Id))
                    && compacted.SequenceEqual(shown.Where(r=>r.CanCompact).Select(r=>r.Id)) && capsuleClicks==0,
                    $"cache list ({name}): 핑, 유지 and 압축 act on their own session, a greyed one does nothing, none pins the list");
            }
            // Draft: a compaction the mod tried, as the log confirms it or not: checked, retried, failed past the lapse.
            SessionRow[] tried=[
                new("00000000-0000-0000-0000-000000000011","Tesira 트레이 관리자","캐시 만료 · 271K",true,"00:59 압축 실패",false,false,true,true),
                new("00000000-0000-0000-0000-000000000012","오버레이 캐시 알림","2분 남음 · 351K",true,"압축 다시 시도 중…",true,false,true,false),
                new("00000000-0000-0000-0000-000000000013","블로그 글 퇴고","4분 남음 · 212K",true,"압축 확인 중…",true,false,true,false),
                new("00000000-0000-0000-0000-000000000014","보고서 정리","24분 남음 · 287K",false,"01:58 자동 압축 예정",true,false,true,true)];
            foreach(var (name,kind) in new[]{("cache-list-tried-dark",OverlayThemeKind.Dark),("cache-list-tried-light",OverlayThemeKind.Light)})
            {
                var listed=claude with { Sessions=tried, HistoryFooter=footer, History=[new HistoryRow("1턴 전","472만","4분 11초","19.2k","60.65")] };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"{name}.png"));
            }
            // Draft: the week's section, the usage bar with its share in it and the week's time line with a clock under it.
            foreach(var (kind,account) in new[]{(OverlayThemeKind.Light,10),(OverlayThemeKind.Dark,35),(OverlayThemeKind.Light,2)})
            {
                var week=$"캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n이번주 24(토)~ · PC {account}% · LAPTOP-1 0% · 다른 곳 0% · 계정 {account}%\n초기화 10-31 (토) 20:00 · 4일 15시간 남음";
                var listed=claude with { Sessions=rows[..2], HistoryFooter=week };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                // The week's section, twice as large.
                var panel=form.CurrentLayout!.PanelBounds;
                var crop=new Rectangle(panel.X,panel.Bottom-(int)(panel.Height*0.52),panel.Width,(int)(panel.Height*0.52));
                using var part=bitmap.Clone(crop,bitmap.PixelFormat);
                using var big=new Bitmap(crop.Width*2,crop.Height*2);
                using(var g=Graphics.FromImage(big)){ g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.DrawImage(part,0,0,big.Width,big.Height); }
                big.Save(Path.Combine(directory,$"week-{account}-{(kind==OverlayThemeKind.Light?"light":"dark")}.png"));
            }
            // Draft: the week while another account is in use: under its name, or the line saying its week is not known yet.
            foreach(var (kind,name,body) in new[]{
                (OverlayThemeKind.Light,"week-who-light",WeeklySplit.Named("동생","이번주 31(토)~ · 나 5% · 다른 곳 20% · 계정 25%")+"\n초기화 11-07 (토) 20:00 · 1일 15시간 남음"),
                (OverlayThemeKind.Dark,"week-who-dark",WeeklySplit.Named("동생","이번주 31(토)~ · 나 5% · 다른 곳 20% · 계정 25%")+"\n초기화 11-07 (토) 20:00 · 1일 15시간 남음"),
                (OverlayThemeKind.Light,"week-who-mine-light","이번주 31(토)~ · 나 6% · 다른 곳 3% · 계정 9%\n초기화 11-07 (토) 20:00 · 1일 15시간 남음"),
                (OverlayThemeKind.Dark,"week-who-mine-dark","이번주 31(토)~ · 나 6% · 다른 곳 3% · 계정 9%\n초기화 11-07 (토) 20:00 · 1일 15시간 남음"),
                (OverlayThemeKind.Light,"week-who-unknown-light",WeeklySplit.Unknown("동생","Claude 앱")),
                (OverlayThemeKind.Dark,"week-who-unknown-dark",WeeklySplit.Unknown("동생","Claude 앱"))})
            {
                var listed=claude with { Sessions=rows[..2], HistoryFooter="캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n"+body };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                using var part=bitmap.Clone(form.CurrentLayout!.PanelBounds.ToRectangle(),bitmap.PixelFormat);
                part.Save(Path.Combine(directory,$"{name}.png"));
            }
            {
                var named=claude with { HistoryFooter=WeeklySplit.Named("동생","이번주 31(토)~ · 나 5% · 다른 곳 20% · 계정 25%") };
                var plain=claude with { HistoryFooter="이번주 31(토)~ · 나 5% · 다른 곳 20% · 계정 25%" };
                Check(named.HistoryPanelRows==plain.HistoryPanelRows && TokenStripForm.HistoryFooterWidth(named,144,100)>TokenStripForm.HistoryFooterWidth(plain,144,100),
                    "week under another account's name: the same rows, the panel wide enough for the name");
            }
            // Draft: the week while the usage API keeps failing, its percent the last one read; and in the login's last days.
            var kst=TimeZoneInfo.CreateCustomTimeZone("KST",TimeSpan.FromHours(9),"KST","KST");
            var signedIn=new DateTimeOffset(2026,11,6,5,0,0,TimeSpan.FromHours(9));
            foreach(var (kind,name,note,percent) in new[]{
                (OverlayThemeKind.Light,"week-stale-light",WeeklyUsageMath.StaleLine(new DateTimeOffset(2026,11,5,2,49,0,TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(),ClaudeUsageApi.NoLogin,kst),47),
                (OverlayThemeKind.Dark,"week-stale-dark",WeeklyUsageMath.StaleLine(new DateTimeOffset(2026,11,5,2,49,0,TimeSpan.FromHours(9)).ToUnixTimeMilliseconds(),ClaudeUsageApi.NoLogin,kst),47),
                (OverlayThemeKind.Light,"week-login-light",WeeklyUsageMath.LoginLine(signedIn.AddDays(2).AddHours(5).AddMinutes(1).ToUnixTimeMilliseconds(),signedIn),58),
                (OverlayThemeKind.Dark,"week-login-dark",WeeklyUsageMath.LoginLine(signedIn.AddDays(2).AddHours(5).AddMinutes(1).ToUnixTimeMilliseconds(),signedIn),58),
                // Claude Code in a terminal signed in to another account (2026-11-07): the week is the app's record only.
                (OverlayThemeKind.Light,"week-cli-light",WeeklyUsageMath.CodeOtherLine("계정 2"),0),
                (OverlayThemeKind.Dark,"week-cli-dark",WeeklyUsageMath.CodeOtherLine("계정 2"),0)})
            {
                var week=$"캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%\n이번주 31(토)~ · PC {percent}% · LAPTOP-1 0% · 다른 곳 0% · 계정 {percent}%\n초기화 11-07 (토) 20:00 · 1일 15시간 남음\n"+note;
                var listed=claude with { Sessions=rows[..2], HistoryFooter=week };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                var panel=form.CurrentLayout!.PanelBounds;
                using var part=bitmap.Clone(panel.ToRectangle(),bitmap.PixelFormat);
                part.Save(Path.Combine(directory,$"{name}.png"));
                // The line is the panel's last row: a click on its text opens the sign-in, one on the bar above does not.
                var logins=0;
                form.LoginClicked+=(_,_)=>logins++;
                var rowHeight=form.CurrentLayout!.ExpandedRowHeight;
                var lastRow=panel.ToRectangle().Bottom-OverlayRenderMetrics.Create(form.CurrentLayout.Dpi,form.CurrentLayout.ScalePercent).PanelPadding-rowHeight/2;
                form.SimulateClick(new Point(panel.ToRectangle().Left+rowHeight,lastRow));
                var onLine=logins;
                form.SimulateClick(new Point(panel.ToRectangle().Left+rowHeight,lastRow-4*rowHeight));
                Check(onLine==1&&logins==1,$"login line ({name}): a click on it asks for the sign-in, a click on the bar does not");
            }
            Check(WeeklySplit.ParseReset("초기화 10-31 (토) 20:00 · 4일 15시간 남음") is { When: "초기화 10-31 (토) 20:00", Left: "4일 15시간 남음" } r1
                    && Math.Abs(r1.Elapsed-(1-(4*24+15)/168.0))<1e-9
                && WeeklySplit.ParseReset("초기화 10-31 (토) 20:00 (추정) · 3시간 20분 남음") is { When: "초기화 10-31 (토) 20:00 (추정)" } r2
                    && Math.Abs(r2.Elapsed-(1-200/10080.0))<1e-9
                && WeeklySplit.ParseReset("초기화 10-31 (토) 20:00 · 5분 남음")?.Elapsed>0.99
                && WeeklySplit.ParseReset("초기화 11-02 (월) 09:14 · 4일 15시간 남음 · 리셋 크레딧 2개") is { Left: "4일 15시간 남음", Extra: " · 리셋 크레딧 2개" }
                && WeeklySplit.ParseReset("초기화 10-31 (토) 20:00 · 4일 15시간 남음 · 다른 것") is null
                && WeeklySplit.ParseReset("캐시 유지 · 60분 남음 · 세션 캐시 읽기 99%") is null
                && WeeklySplit.ParseReset(WeeklyUsageMath.ResetLine(DateTimeOffset.Parse("2026-10-31T11:00:00Z").ToUnixTimeMilliseconds(),
                    DateTimeOffset.Parse("2026-10-27T10:00:00Z"))) is { Left: "4일 1시간 남음" },
                "week's time line: the reset line gives how long is left and the share of the week gone by");
            var codexList=plainTwo with { App=HostApp.Codex, History=[new HistoryRow("1턴 전","472만","4분 11초","19.2k","60.65")] };
            // Draft: Codex's week, whose reset line ends with its reset credits.
            foreach(var kind in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
            {
                var listed=codexList with { HistoryFooter="세션 캐시 읽기 96%\n이번주 26(월)~ · PC 3% · LAPTOP-1 1% · 다른 곳 51% · 계정 55%\n초기화 11-02 (월) 09:14 · 4일 15시간 남음 · 리셋 크레딧 2개" };
                using var form=new TokenStripForm();
                _=form.Handle;
                form.SetPresentation(listed);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetShowHistory(true);
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,listed.HistoryPanelRows,false,
                    Presentation:listed,CompactPanel:true)));
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(directory,$"codex-week-{(kind==OverlayThemeKind.Light?"light":"dark")}.png"));
                var plain=listed with { HistoryFooter=listed.HistoryFooter!.Replace(" · 리셋 크레딧 2개","") };
                Check(listed.HistoryPanelRows==plain.HistoryPanelRows && TokenStripForm.HistoryFooterWidth(listed,144,100)>TokenStripForm.HistoryFooterWidth(plain,144,100),
                    $"codex week ({kind}): the reset line with its credits gets the time line too, the panel wide enough for them");
            }
            Check(codexList.Sessions is null && codexList.HistoryPanelRows==2 && (claude with { Sessions=[] }).HasHistoryPanel,
                "cache list: Codex keeps its recent turns; Claude's list opens even with no session to ping");
        }
        // Draft: the device manager with another person's devices.
        {
            var shareRoot=Path.Combine(directory,"device-manager-share");
            if(Directory.Exists(shareRoot)) Directory.Delete(shareRoot,true);
            var devices=Path.Combine(shareRoot,DriveShare.FolderName,"devices");
            Directory.CreateDirectory(devices);
            foreach(var (id,shownName) in new[]{("pc-me","DESKTOP-1"),("pc-lap","LAPTOP-1"),("pc-old","LAPTOP-OLD"),("pc-f","FRIEND-PC")})
                File.WriteAllText(Path.Combine(devices,$"{id}.claude.json"),System.Text.Json.JsonSerializer.Serialize(new{device=id,name=shownName}));
            var share=new DriveShare(devices,"pc-me","DESKTOP-1");
            share.Rename("pc-me","PC"); share.Rename("pc-lap","노트북"); share.Rename("pc-old","랩탑");
            share.SetOwner("pc-f","친구1"); share.SetDefaultOwner("AB");
            share.ReadShared("claude",DateTimeOffset.UtcNow).ForEach(f=>f.Document.Dispose());
            using var manager=new DeviceManagerForm(new DriveShareLink(()=>share,true),_=>{});
            manager.StartPosition=FormStartPosition.Manual; manager.Location=new Point(-20000,-20000); manager.ShowInTaskbar=false;
            manager.Show();
            manager.Fill();
            Application.DoEvents();
            using var bitmap=new Bitmap(manager.Width,manager.Height);
            manager.DrawToBitmap(bitmap,new Rectangle(0,0,manager.Width,manager.Height));
            bitmap.Save(Path.Combine(directory,"device-manager-people.png"));
            manager.Close();
        }
        // Draft: chat column beside a side panel at 200 %, as measured, with the capsule at its top right.
        {
            var window=new CodexWindowInfo(new IntPtr(1),new IntRect(-8,-8,3856,2080),new IntRect(0,0,3840,2064),null,new IntRect(0,0,3840,2064),192,
                new WindowChromeMetrics(36,24,4,4,4));
            var chat=new ClaudePane(630,-1488,null,null,true).Host(window);
            var ringed=TurnPresentation.Create(Live(0.75));
            var layout=OverlayLayoutCalculator.Calculate(new(chat,AnchorMode.InsideTopRight,false,0,false,Presentation:ringed));
            cases.Add(new {Draft="side-panel-top",layout.WindowBounds,layout.CapsuleBounds,layout.RingBounds});
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(ringed);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(layout);
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,"side-panel-top.png"));
        }
        // Chat not known yet: Ctrl opens the weekly line alone (drawn for review).
        {
            var line=WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(),null,38,31,0,ThisPcOnly:22),new WeeklySplit.Names("PC","랩탑"));
            var waiting=OverlayPresentationBuilder.CreateWaiting("현재 채팅 확인 대기",DisplayField.Total,DisplayField.ContextPercent,DisplayField.Total)
                with { App=HostApp.Claude, HistoryFooter=line+"\n"+WeeklyUsageMath.ResetLine(DateTimeOffset.Parse("2026-10-24T11:00:00Z").ToUnixTimeMilliseconds(),compactedAt) };
            using var form=new TokenStripForm();
            _=form.Handle;
            form.SetPresentation(waiting);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.SetShowHistory(true);
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144,4000),AnchorMode.InsideTopRight,true,waiting.HistoryPanelRows,false,
                Presentation:waiting,CompactPanel:true)));
            using var bitmap=new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory,"waiting-weekly.png"));
        }
        var expandedRing=OverlayLayoutCalculator.Calculate(new(Host(96,1920),AnchorMode.InsideTopRight,true,10,false,Presentation:plainTwo with {ContextFill=0.5}));
        Check(expandedRing.RingBounds.IsEmpty && OverlayLayoutCalculator.Calculate(new(Host(96,4000),AnchorMode.InsideTopRight,false,0,false,
            Presentation:plainTwo with {ContextFill=0.5,StatusText="확인 중…"})).RingBounds.IsEmpty,"no ring on the open panel or a waiting capsule");
        Check(AutoCapsule.SplitNoteCount("캐시 만료 109.0k")==("캐시 만료","109.0k") && AutoCapsule.SplitNoteCount("캐시 만료 1.20M") is not null
            && AutoCapsule.SplitNoteCount("압축됨 · 새로 저장") is null && AutoCapsule.SplitNoteCount("캐시 47분 남음") is null
            && AutoCapsule.SplitNoteCount("캐시히트 99.1%") is null && AutoCapsule.SplitNotePercent("캐시히트 99.1%")==("캐시히트","99.1%")
            && AutoCapsule.SplitNotePercent("캐시 만료 109.0k") is null && AutoCapsule.SplitNotePercent("캐시 47분 남음") is null,
            "only a note ending in a token count is split into the count column; one ending in a share goes to the lower left");
        return new {Passed=checks.Count,Checks=checks,Cases=cases};
    }
}
