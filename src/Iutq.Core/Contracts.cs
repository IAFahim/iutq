namespace Iutq.Core;

public interface IClipFrameVisitor<TClip>
    where TClip : unmanaged
{
    void Visit(in ClipHit hit, in TClip clip);
}

public interface IClipTransitionVisitor<TClip>
    where TClip : unmanaged
{
    void Clip(in ClipTransition transition, in TClip clip);
    void Blend(in BlendTransition transition, in TClip clipA, in TClip clipB);
}
