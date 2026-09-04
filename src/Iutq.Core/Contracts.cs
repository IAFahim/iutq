namespace Iutq.Core;

public interface IClipSampleVisitor<TClip>
    where TClip : unmanaged
{
    void Sample(in TrackInstance track, in TClip clip, float weight);
}

public interface IClipFrameVisitor<TClip>
    where TClip : unmanaged
{
    void Visit(in TrackInstance track, in ClipHit hit, in TClip clip);
}

public interface IClipTransitionVisitor<TClip>
    where TClip : unmanaged
{
    void Clip(in TrackInstance track, in ClipTransition transition, in TClip clip);
    void Blend(in TrackInstance track, in BlendTransition transition, in TClip clipA, in TClip clipB);
}
