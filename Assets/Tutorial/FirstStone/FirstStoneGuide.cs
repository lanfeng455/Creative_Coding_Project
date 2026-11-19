using UnityEngine;

// Scene-local to the opening foundation. The writer and the builder still own progress.
public sealed class FirstStoneGuide : MonoBehaviour
{
    B1EventScheduler rain;
    SlingshotBand band;
    FirstStonePrompts prompts;
    PhysicsLetter firstS;
    Vector2 left, right;
    bool haveLeft, haveRight, paired, previousPair, attempt, recorded, finished;
    float ceiling, leftLift, rightLift, retryAt, cueAt = -1, fade;
    int misses;

    internal void Bind(B1EventScheduler scheduler, SlingshotBand slingshot)
    {
        rain = scheduler;
        band = slingshot;
        var field = FindFirstObjectByType<CyberControlVisual>();
        ceiling = field ? field.TopEdge : rain.world.upperFloorY - .54f;
        prompts = new FirstStonePrompts(transform, rain.world, ceiling, band);
        band.OpeningGuide = this;
        rain.dino.WatchingFirstStone = true;
        rain.dino.ConstructionStepInstalled += Installed;
        rain.typeWriter.LetterAdded += Added;
    }

    internal void ObserveTips(int count, Vector2 a, Vector2 b)
    {
        if (recorded || finished) return;
        var world = rain.world;
        float middle = (world.boundsXMin + world.boundsXMax) * .5f;
        bool usableA = count > 0 && InPlayfield(a);
        bool usableB = count == 2 && InPlayfield(b);
        bool singleLeft = a.x <= middle;
        haveLeft = usableA && (count == 2 || singleLeft);
        haveRight = count == 2 ? usableB : usableA && !singleLeft;
        Vector2 newRight = count == 2 ? b : a;
        paired = haveLeft && haveRight && b.x - a.x >= .8f;

        float now = Time.time;
        if (paired && previousPair && Time.deltaTime > 0)
        {
            float step = .5f * Time.deltaTime;
            if (a.y - left.y > step) leftLift = now;
            if (newRight.y - right.y > step) rightLift = now;
        }
        else leftLift = rightLift = float.NegativeInfinity;
        previousPair = paired;
        left = a;
        right = newRight;
    }

    bool InPlayfield(Vector2 p) => p.y < ceiling && p.y > rain.world.lowerGroundY &&
        p.x >= rain.world.boundsXMin && p.x <= rain.world.boundsXMax;

    internal bool AllowsFling(PhysicsLetter letter) => letter != firstS ||
        (paired && Time.time - leftLift <= band.velWindow && Time.time - rightLift <= band.velWindow);

    // True while this tutorial owns the next drop. False hands timing and physics back to the scheduler.
    internal bool GuideDrop()
    {
        if (finished) return false;
        if (!rain.dino.AwaitingFoundation)
        {
            Finish();
            return false;
        }
        bool waiting = rain.dino.IsConstructionRequest("STONE");
        rain.dino.WatchingFirstStone = waiting && !recorded;
        if (recorded)
        {
            if (fade < 1)
            {
                fade = Mathf.MoveTowards(fade, 1, Time.deltaTime / .32f);
                prompts.Fade(1 - fade);
            }
            return !waiting;
        }
        if (!waiting) return true;

        if (!band || !band.isActiveAndEnabled) ObserveTips(0, default, default);
        float now = Time.time;
        if (attempt && !firstS)
        {
            attempt = false;
            misses++;
            retryAt = now + .55f;
            cueAt = -1;
        }
        if (paired && !attempt && now >= retryAt)
        {
            Vector2 mid = (left + right) * .5f;
            float y = Mathf.Min(ceiling + .34f, mid.y + 1.35f);
            firstS = rain.DropLetter('S', new Vector2(mid.x, y), .35f, true);
            attempt = true;
        }

        bool cue = false;
        if (paired && firstS && firstS.Vel.y < 0)
        {
            Vector2 p = firstS.transform.position;
            float bandY = Mathf.Lerp(left.y, right.y, Mathf.InverseLerp(left.x, right.x, p.x));
            bool overBand = p.x >= left.x-firstS.Radius && p.x <= right.x+firstS.Radius;
            float distance = p.y - bandY;
            float contact = firstS.Radius + band.bandHalfWidth;
            cue = overBand && distance >= -contact && distance <= contact + .23f + .34f * -firstS.Vel.y;
            if (cue && cueAt < 0) cueAt = now;
        }
        bool demonstrate = misses == 0 || (misses >= 2 && misses % 2 == 0);
        prompts.Draw(haveLeft, left, haveRight, right, paired, cue,
            cue && demonstrate ? Mathf.Clamp01((now - cueAt) / .46f) : -1);
        return true;
    }

    void Added(char letter)
    {
        if (recorded || letter != 'S' || !rain.dino.IsConstructionRequest("STONE")) return;
        recorded = true;
        rain.dino.WatchingFirstStone = false;
        rain.dino.FlashStoneInitial();
        band.OpeningGuide = null;
    }

    void Installed(int step)
    {
        if (step >= 1) Finish();
    }

    void Finish()
    {
        finished = true;
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (band) band.OpeningGuide = null;
        if (rain && rain.typeWriter) rain.typeWriter.LetterAdded -= Added;
        if (rain && rain.dino)
        {
            rain.dino.ConstructionStepInstalled -= Installed;
            rain.dino.WatchingFirstStone = false;
        }
        prompts?.Dispose();
    }
}
