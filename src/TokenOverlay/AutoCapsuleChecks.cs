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
            Check(!off.FullTopRow && off.CpuBarBounds.IsEmpty && on.FullTopRow
                && on.RingModuleBounds==new IntRect(on.CapsuleBounds.X,on.RingBounds.Y,on.CapsuleBounds.Width,on.RingBounds.Height)
                && on.RingBounds==off.RingBounds && on.RingNoteBounds==off.RingNoteBounds && on.CapsuleBounds==off.CapsuleBounds && on.WindowBounds==off.WindowBounds
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
                    && !shape.IsVisible(on.CapsuleBounds.X,on.RingBounds.Top,probe) && !shape.IsVisible(on.CapsuleBounds.Right-1,on.CapsuleBounds.Bottom-1,probe)
                    && form.WindowShapeContains(on.CapsuleBounds.X+1,on.RingBounds.Top+m.CapsuleRadius+1),
                    $"CPU meter: the window is one rounded rectangle over the row and the capsule ({dpi} DPI)");
            if(dpi!=144) continue;
            foreach(var kind in new[]{OverlayThemeKind.Light,OverlayThemeKind.Dark})
            foreach(var (cpu,ram,page) in new (double?,double?,double?)[]{(0.07,0.84,0.03),(0.43,0.62,0.28),(0.87,0.95,0.71),(null,null,null)})
            {
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.SetMeters(cpu,ram,page);
                using var shot=Shot(form);
                shot.Save(Path.Combine(directory,$"cpu-{(kind==OverlayThemeKind.Light?"light":"dark")}-{(cpu is double c?$"{c*100:0}":"none")}.png"));
            }
            // Each whole percent shows: 42% and 43% differ inside the bar.
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.SetMeters(0.42,0.5,0.5);
            using var at42=Shot(form);
            form.SetMeters(0.43,0.5,0.5);
            using var at43=Shot(form);
            var differs=false;
            for(var py=bar.Top;py<bar.Bottom && !differs;py++)
                // Near the bar's left edge, clear of the number in its middle.
                if(at42.GetPixel(bar.Left+bar.Width/8+2,py)!=at43.GetPixel(bar.Left+bar.Width/8+2,py)) differs=true;
            Check(differs && form.CpuFill==0.43,"CPU meter: one percent more fills a little more of the bar");
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
            form.SetMeters(0.31,0.84,0.03);
            using var shot=Shot(form);
            shot.Save(Path.Combine(directory,"cpu-alone-dark.png"));
        }
        Check(CpuUsage.Between((100,1000),(150,1100))==0.5 && CpuUsage.Between((100,1000),(100,1000)) is null
            && CpuUsage.Between((0,0),(200,200))==0 && TokenStripForm.CpuPercent(0.426)==43 && TokenStripForm.CpuPercent(1.3)==100 && TokenStripForm.CpuPercent(null) is null,
            "CPU meter: the share from Windows' time totals, shown in whole percents");
        using(var live=new CpuUsage()) { live.Sample(); System.Threading.Thread.Sleep(300); Check(live.Sample() is >= 0 and <= 1,"CPU meter: a live reading from Windows");
            Check(CpuUsage.Memory() is > 0 and <= 1 && live.PageFile() is null or (>= 0 and <= 1),"Meters: live memory and page file readings from Windows"); }
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
            var listed=plainTwo with { History=[new HistoryRow("1턴 전","4.7M","4분 11초","19.2k","60.65")],
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
            var listed=plainTwo with { History=[new HistoryRow("1턴 전","4.7M","4분 11초","19.2k","60.65")],
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
                var listed=claude with { Sessions=shown, HistoryFooter=footer, History=[new HistoryRow("1턴 전","4.7M","4분 11초","19.2k","60.65")] };
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
                Check(listed.HistoryPanelRows==Math.Max(1,shown.Length)+5 && buttons.Count==3*shown.Length
                    && buttons.All(b=>layout.PanelBounds.ToRectangle().Contains(b.Bounds))
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
                var listed=claude with { Sessions=tried, HistoryFooter=footer, History=[new HistoryRow("1턴 전","4.7M","4분 11초","19.2k","60.65")] };
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
            var codexList=plainTwo with { App=HostApp.Codex, History=[new HistoryRow("1턴 전","4.7M","4분 11초","19.2k","60.65")] };
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
