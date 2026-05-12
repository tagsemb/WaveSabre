using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WaveSabreConvert
{
    public class LiveConverter
    {
        ILog logger;

        class Receive
        {
            public LiveProject.Track SendingTrack;
            public int ReceivingChannelIndex;
            public double Volume;
            public List<LiveProject.Event> Envelope;
            public Receive(LiveProject.Track sendingTrack, int receivingChannelIndex, double volume, List<LiveProject.Event> envelope)
            {
                SendingTrack = sendingTrack;
                ReceivingChannelIndex = receivingChannelIndex;
                Volume = volume;
                Envelope = envelope;
            }
        }

        class Event
        {
            public double Time;
            public int Samples;
            public Song.EventType Type;
            public byte Note;
            public byte Velocity;
        }

        Dictionary<LiveProject.Track, List<Receive>> trackReceives;

        List<LiveProject.Track> visitedTracks, orderedTracks;

        public Song Process(LiveProject project, ILog logger)
        {
            this.logger = logger;

            propagateGroupEnvelopes(project, logger);

            var song = new Song();

            song.Tempo = (int)project.Tempo;
            song.SampleRate = 44100;

            var projectLoopEnd = project.LoopStart + project.LoopLength;

            trackReceives = new Dictionary<LiveProject.Track, List<Receive>>();
            foreach (var projectTrack in project.Tracks) trackReceives.Add(projectTrack, new List<Receive>());
            foreach (var projectTrack in project.Tracks)
            {
                foreach (var send in projectTrack.Sends)
                {
                    if (send.IsActive) trackReceives[send.ReceivingTrack].Add(new Receive(projectTrack, send.ReceivingChannelIndex - 1, send.Volume, send.Envelope));
                }
            }

            project.MasterTrack.Name = project.MasterTrack.Name == "" ? "Master" : project.MasterTrack.Name;

            visitedTracks = new List<LiveProject.Track>();
            orderedTracks = new List<LiveProject.Track>();

            visitTrack(project.MasterTrack);

            var projectTracksToSongTracks = new Dictionary<LiveProject.Track, Song.Track>();
            var songTrackEvents = new Dictionary<Song.Track, List<Event>>();

            int? minEventTime = null;
            int? maxEventTime = null;

            foreach (var projectTrack in orderedTracks)
            {
                var track = new Song.Track();
                track.Name = projectTrack.Name;
                track.Volume = (float)projectTrack.Volume;
                // Remap Ableton's pan (-1..+1, 0 = center) to runtime convention (0..1, 0.5 = center).
                track.Pan = (float)((projectTrack.Pan + 1.0) * 0.5);

                foreach (var projectDevice in projectTrack.Devices)
                {
                    if (projectDevice.PluginDll == null)
                    {
                        logger.WriteLine("WARNING: Device skipped (unsupported plugin with no DLL, probably VST3)");
                        continue;
                    }

                    Song.Device device = null;

                    Song.DeviceId deviceId;
                    if (Enum.TryParse<Song.DeviceId>(projectDevice.PluginDll.Replace(".dll", "").Replace(".64", ""), out deviceId))
                    {
                        device = new Song.Device();
                        device.Id = deviceId;
                        device.Chunk = projectDevice.RawData != null ? (byte[])projectDevice.RawData.Clone() : new byte[0];
                    }
                    if (device == null)
                    {
                        logger.WriteLine("WARNING: Device skipped (unsupported plugin): " + projectDevice.PluginDll);
                    }
                    else if (projectDevice.Bypass)
                    {
                        logger.WriteLine("WARNING: Device skipped (bypass enabled): " + projectDevice.PluginDll);
                    }
                    else
                    {
                        track.Devices.Add(device);

                        foreach (var floatParameter in projectDevice.FloatParameters)
                        {
                            if (floatParameter.Id >= 0)
                            {
                                var automation = new Song.Automation();
                                automation.DeviceIndex = track.Devices.IndexOf(device);
                                automation.ParamId = floatParameter.Id;
                                foreach (var e in floatParameter.Events)
                                {
                                    if (e.Time >= 0.0)
                                    {
                                        var point = new Song.Point();
                                        point.TimeStamp = secondsToSamples(e.Time, song.Tempo, song.SampleRate);
                                        point.Value = e.Value;
                                        automation.Points.Add(point);
                                    }
                                }
                                if (automation.Points.Count > 0) track.Automations.Add(automation);
                            }
                        }
                    }
                }

                if (projectTrack.VolumeEnvelope != null)
                {
                    var auto = makeMixerAutomation(Song.MixerTarget.Volume, 0, projectTrack.VolumeEnvelope, song, false);
                    if (auto.Points.Count > 0) track.MixerAutomations.Add(auto);
                }
                if (projectTrack.PanEnvelope != null)
                {
                    var auto = makeMixerAutomation(Song.MixerTarget.Pan, 0, projectTrack.PanEnvelope, song, true);
                    if (auto.Points.Count > 0) track.MixerAutomations.Add(auto);
                }

                var events = new List<Event>();
                foreach (var midiClip in projectTrack.MidiClips)
                {
                    if (!midiClip.IsDisabled)
                    {
                        var loopLength = midiClip.LoopEnd - midiClip.LoopStart;
                        for (var currentTime = midiClip.CurrentStart; currentTime < midiClip.CurrentEnd; currentTime += loopLength)
                        {
                            foreach (var keyTrack in midiClip.KeyTracks)
                            {
                                foreach (var note in keyTrack.Notes)
                                {
                                    if (note.IsEnabled)
                                    {
                                        var startTime = note.Time - (currentTime - midiClip.CurrentStart) - midiClip.LoopStartRelative;
                                        while (startTime < 0.0) startTime += loopLength;
                                        startTime = currentTime + startTime - midiClip.LoopStart;
                                        var endTime = startTime + note.Duration;

                                        if ((startTime >= midiClip.CurrentStart && startTime < midiClip.CurrentEnd) &&
                                            (!project.IsLoopOn || (
                                                startTime >= project.LoopStart && startTime < projectLoopEnd)))
                                        {
                                            endTime = Math.Min(endTime, midiClip.CurrentEnd);
                                            if (project.IsLoopOn) endTime = Math.Min(endTime, projectLoopEnd);
                                            if (endTime > startTime)
                                            {
                                                var startEvent = new Event();
                                                startEvent.Time = startTime;
                                                startEvent.Samples = secondsToSamples(startTime, song.Tempo, song.SampleRate);
                                                startEvent.Type = Song.EventType.NoteOn;
                                                startEvent.Note = (byte)keyTrack.MidiKey;
                                                startEvent.Velocity = (byte)note.Velocity;
                                                events.Add(startEvent);

                                                var endEvent = new Event();
                                                endEvent.Time = endTime;
                                                endEvent.Samples = secondsToSamples(endTime, song.Tempo, song.SampleRate);
                                                endEvent.Type = Song.EventType.NoteOff;
                                                endEvent.Note = (byte)keyTrack.MidiKey;
                                                events.Add(endEvent);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                events.Sort((a, b) =>
                {
                    if (a.Samples > b.Samples) return 1;
                    if (a.Samples < b.Samples) return -1;
                    if (a.Type == Song.EventType.NoteOn && b.Type == Song.EventType.NoteOff) return 1;
                    if (a.Type == Song.EventType.NoteOff && b.Type == Song.EventType.NoteOn) return -1;
                    return 0;
                });
                foreach (var e in events)
                {
                    if (!minEventTime.HasValue || e.Samples < minEventTime.Value) minEventTime = e.Samples;
                    if (!maxEventTime.HasValue || e.Samples > maxEventTime.Value) maxEventTime = e.Samples;
                }

                projectTracksToSongTracks.Add(projectTrack, track);
                songTrackEvents.Add(track, events);
                song.Tracks.Add(track);
            }

            double songStartTime, songEndTime;
            if (project.IsLoopOn)
            {
                songStartTime = project.LoopStart;
                songEndTime = projectLoopEnd;
            }
            else if (minEventTime.HasValue && maxEventTime.HasValue)
            {
                songStartTime = samplesToSeconds(minEventTime.Value, song.Tempo, song.SampleRate);
                songEndTime = samplesToSeconds(maxEventTime.Value, song.Tempo, song.SampleRate);
            }
            else
            {
                throw new Exception("Couldn't find song start/end times");
            }
            song.Length = (songEndTime - songStartTime) * 60.0 / (double)song.Tempo;

            foreach (var kvp in songTrackEvents)
            {
                var track = kvp.Key;
                var events = kvp.Value;

                int lastTimeStamp = 0;
                foreach (var e in events)
                {
                    var songEvent = new Song.Event();
                    var time = e.Time - songStartTime;
                    int timeStamp = Math.Max(secondsToSamples(time, song.Tempo, song.SampleRate), lastTimeStamp);

                    songEvent.TimeStamp = timeStamp;
                    songEvent.Type = e.Type;
                    songEvent.Note = e.Note;
                    songEvent.Velocity = e.Velocity;
                    track.Events.Add(songEvent);
                    lastTimeStamp = timeStamp;
                }
            }

            // TODO: Clip all of this instead of just offsetting
            // adjust automation start times based on song start
            int songStartSamples = secondsToSamples(songStartTime, song.Tempo, song.SampleRate);
            foreach (var track in song.Tracks)
            {
                foreach (var automation in track.Automations)
                {
                    foreach (var point in automation.Points)
                        point.TimeStamp -= songStartSamples;
                }
                foreach (var automation in track.MixerAutomations)
                {
                    foreach (var point in automation.Points)
                        point.TimeStamp -= songStartSamples;
                }
            }

            foreach (var kvp in projectTracksToSongTracks)
            {
                int sendIndex = 0;
                foreach (var projectReceive in trackReceives[kvp.Key])
                {
                    if (projectTracksToSongTracks.ContainsKey(projectReceive.SendingTrack))
                    {
                        var receive = new Song.Receive();
                        receive.SendingTrackIndex = song.Tracks.IndexOf(projectTracksToSongTracks[projectReceive.SendingTrack]);
                        receive.ReceivingChannelIndex = projectReceive.ReceivingChannelIndex;
                        receive.Volume = (float)projectReceive.Volume;
                        kvp.Value.Receives.Add(receive);

                        if (projectReceive.Envelope != null)
                        {
                            var auto = makeMixerAutomation(Song.MixerTarget.SendVolume, sendIndex, projectReceive.Envelope, song, false);
                            for (int p = 0; p < auto.Points.Count; p++) auto.Points[p].TimeStamp -= songStartSamples;
                            if (auto.Points.Count > 0) kvp.Value.MixerAutomations.Add(auto);
                        }

                        sendIndex++;
                    }
                }
            }

            return song;
        }

        // Replicate group-track volume/pan envelopes onto each child track.
        // Pointwise-multiply when both group and child have volume envelopes; child wins for pan.
        // After propagation the group's own mixer envelopes are cleared so they don't double-apply
        // (the group track remains in the output as the audio routing hub for its children).
        static void propagateGroupEnvelopes(LiveProject project, ILog logger)
        {
            var children = new Dictionary<string, List<LiveProject.Track>>();
            foreach (var pt in project.Tracks)
            {
                if (string.IsNullOrEmpty(pt.TrackGroupId) || pt.TrackGroupId == "-1") continue;
                if (!children.ContainsKey(pt.TrackGroupId))
                    children[pt.TrackGroupId] = new List<LiveProject.Track>();
                children[pt.TrackGroupId].Add(pt);
            }

            // Iterate in project order so outer groups propagate to (possibly nested) inner groups
            // first; subsequent inner-group passes then carry the merged envelope to leaf children.
            foreach (var groupTrack in project.Tracks)
            {
                if (!groupTrack.IsGroupTrack) continue;
                if (!children.ContainsKey(groupTrack.Id)) continue;

                var groupChildren = children[groupTrack.Id];

                if (groupTrack.VolumeEnvelope != null)
                {
                    foreach (var child in groupChildren)
                    {
                        if (child.VolumeEnvelope == null)
                        {
                            // Child has no envelope of its own — its static fader value IS its
                            // effective volume. Once we assign an envelope here, the runtime
                            // ignores the static fader entirely (volumeAutomation overrides
                            // volume in SongRenderer::Track), so we fold it into the envelope
                            // and reset the static to unity. Without this, lowering a child
                            // track's fader has no effect once the group automation propagates.
                            child.VolumeEnvelope = scaleEnvelope(groupTrack.VolumeEnvelope, (float)child.Volume);
                            child.Volume = 1.0;
                        }
                        else
                        {
                            // Child already has an envelope — its static fader is already
                            // overridden (in both Ableton and the runtime), so we just
                            // pointwise-multiply the two envelopes and leave the static alone.
                            child.VolumeEnvelope = multiplyEnvelopes(groupTrack.VolumeEnvelope, child.VolumeEnvelope);
                        }
                    }
                    groupTrack.VolumeEnvelope = null;
                    // Reset the group's static volume to unity. Ableton's Manual value is ignored when
                    // automation is active; leaving it in would double-apply on the now-flat group bus.
                    groupTrack.Volume = 1.0;
                    logger.WriteLine("Propagated group '{0}' volume envelope to {1} child(ren)", groupTrack.Name, groupChildren.Count);
                }

                if (groupTrack.PanEnvelope != null)
                {
                    foreach (var child in groupChildren)
                    {
                        if (child.PanEnvelope == null)
                        {
                            // Child has no pan envelope of its own — its static fader IS its pan.
                            // Fold it into the group's envelope (clamped sum in Ableton's -1..+1 space)
                            // and reset the static to center, mirroring the volume case.
                            child.PanEnvelope = offsetEnvelopeClamped(groupTrack.PanEnvelope, (float)child.Pan, -1.0f, 1.0f);
                            child.Pan = 0.0;
                        }
                        else
                        {
                            // Both have envelopes. Nested balance controls can't flatten perfectly
                            // into one pan stage; clamped pointwise sum is the practical
                            // approximation — "group pan offsets child pan", with extremes saturated.
                            child.PanEnvelope = addEnvelopesClamped(groupTrack.PanEnvelope, child.PanEnvelope, -1.0f, 1.0f);
                        }
                    }
                    groupTrack.PanEnvelope = null;
                    groupTrack.Pan = 0.0;
                    logger.WriteLine("Propagated group '{0}' pan envelope to {1} child(ren)", groupTrack.Name, groupChildren.Count);
                }
            }
        }

        static List<LiveProject.Event> scaleEnvelope(List<LiveProject.Event> envelope, float scale)
        {
            if (scale == 1.0f) return envelope;
            var result = new List<LiveProject.Event>();
            foreach (var e in envelope)
            {
                result.Add(new LiveProject.Event
                {
                    Time = e.Time,
                    Value = e.Value * scale
                });
            }
            return result;
        }

        // Add a constant to every point of an envelope, clamped to [min, max].
        // Used for folding a track's static pan into a propagated group pan envelope.
        static List<LiveProject.Event> offsetEnvelopeClamped(List<LiveProject.Event> envelope, float offset, float min, float max)
        {
            if (offset == 0.0f) return envelope;
            var result = new List<LiveProject.Event>();
            foreach (var e in envelope)
            {
                float v = e.Value + offset;
                if (v < min) v = min;
                else if (v > max) v = max;
                result.Add(new LiveProject.Event { Time = e.Time, Value = v });
            }
            return result;
        }

        // Pointwise sum two envelopes (sampled at the union of their time points), clamped to [min, max].
        // Used to combine a group's pan envelope with a child's pan envelope when both exist; the
        // clamped sum is the practical one-stage approximation of nested balance controls.
        static List<LiveProject.Event> addEnvelopesClamped(List<LiveProject.Event> a, List<LiveProject.Event> b, float min, float max)
        {
            var times = new SortedSet<double>();
            foreach (var e in a) times.Add(e.Time);
            foreach (var e in b) times.Add(e.Time);

            var result = new List<LiveProject.Event>();
            foreach (var t in times)
            {
                float v = evaluateEnvelopeAt(a, t) + evaluateEnvelopeAt(b, t);
                if (v < min) v = min;
                else if (v > max) v = max;
                result.Add(new LiveProject.Event { Time = t, Value = v });
            }
            return result;
        }

        static List<LiveProject.Event> multiplyEnvelopes(List<LiveProject.Event> a, List<LiveProject.Event> b)
        {
            var times = new SortedSet<double>();
            foreach (var e in a) times.Add(e.Time);
            foreach (var e in b) times.Add(e.Time);

            var result = new List<LiveProject.Event>();
            foreach (var t in times)
            {
                result.Add(new LiveProject.Event
                {
                    Time = t,
                    Value = evaluateEnvelopeAt(a, t) * evaluateEnvelopeAt(b, t)
                });
            }
            return result;
        }

        static float evaluateEnvelopeAt(List<LiveProject.Event> events, double time)
        {
            if (events.Count == 0) return 1.0f;
            if (time <= events[0].Time) return events[0].Value;
            if (time >= events[events.Count - 1].Time) return events[events.Count - 1].Value;
            for (int i = 1; i < events.Count; i++)
            {
                if (events[i].Time >= time)
                {
                    double dt = events[i].Time - events[i - 1].Time;
                    if (dt <= 0) return events[i].Value;
                    float f = (float)((time - events[i - 1].Time) / dt);
                    return events[i - 1].Value + (events[i].Value - events[i - 1].Value) * f;
                }
            }
            return events[events.Count - 1].Value;
        }

        static Song.MixerAutomation makeMixerAutomation(Song.MixerTarget target, int sendIndex, List<LiveProject.Event> envelope, Song song, bool remapPanRange)
        {
            var auto = new Song.MixerAutomation { Target = target, SendIndex = sendIndex };
            foreach (var e in envelope)
            {
                if (e.Time >= 0.0)
                {
                    var point = new Song.Point();
                    point.TimeStamp = secondsToSamples(e.Time, song.Tempo, song.SampleRate);
                    point.Value = remapPanRange ? (e.Value + 1.0f) * 0.5f : e.Value;
                    auto.Points.Add(point);
                }
            }
            return auto;
        }

        void visitTrack(LiveProject.Track projectTrack)
        {
            if (visitedTracks.Contains(projectTrack) || !projectTrack.IsSpeakerOn) return;
            visitedTracks.Add(projectTrack);
            foreach (var projectReceive in trackReceives[projectTrack])
            {
                if (projectReceive.Volume > 0.0) visitTrack(projectReceive.SendingTrack);
            }
            orderedTracks.Add(projectTrack);
        }

        static int secondsToSamples(double time, int tempo, int sampleRate)
        {
            return (int)(time * 60.0 / tempo * sampleRate);
        }

        static double samplesToSeconds(int samples, int tempo, int sampleRate)
        {
            return (double)samples / sampleRate * tempo / 60.0;
        }
    }
}
