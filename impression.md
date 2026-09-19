# The Boxer

### Most boxing games play you an animation of a punch. This one throws one.

---

## The idea

Press a button in a fighting game and you are asking for a clip. The clip plays. Somewhere
inside it, a frame is marked "this is the hit", and if the other fighter's hurtbox happens to
be there on that frame, damage is applied. The punch was never travelling anywhere. It was a
lookup.

**The Boxer never moves the fist directly.** There is no line anywhere in this project that
reads `hand.position = target.position`, and no inverse-kinematics solver is permitted to
teleport a glove into an opponent's face. A physical fist — a real Rigidbody, on real joints,
with real mass — is asked to reach a place, and the arm, the shoulder, the torso and the feet
resolve what happens next.

Everything good about the game falls out of that one refusal.

Because the fist is physical, a punch that gets blocked doesn't need a "blocked punch"
animation — it is simply a mass that met resistance. A punch thrown while you are
off-balance lands soft, because the balance controller is the same system that would have
driven your weight into it. A shot that catches a man already falling does what it would do.
None of these are cases anyone wrote. They are consequences.

```
Input ──▶ Punch Target ──▶ Physics ──▶ Active Ragdoll ──▶ IK ──▶ Character
```

---

## Punching is a decision, not a button

Both triggers are hands. Hold one and that hand *prepares* — the weight shifts, the shoulder
loads, the punch is coming and your opponent can see it coming. Release and it goes.

Hold longer and it hits harder, up to a cap. But holding burns stamina past a short grace
window, and as the boxer tires that bonus bleeds away to nothing — so late in a fight the
big shot you have been winding up is no longer there, and the fighter who paced himself
still has his. Tap instead of hold and you get the fast, cheap, uncommitted version.

While the hand is loaded, the stick chooses its shape: up for a hook, down for an uppercut,
neutral for the straight. Raise your guard instead and the punch is cancelled — you spent
the stamina and got nothing, which is exactly the trade a real fighter makes when he thinks
better of it mid-throw.

| | |
| --- | --- |
| **Left stick** | Footwork. Always. |
| **Right stick** | Lean — slip, roll, ride a shot in any direction |
| **L2 / R2** | Hold to load that hand, release to throw |
| **R1 / L1** | Guard high / guard the body |

Movement is in the boxer's own frame, not the world's. He is always squared up on his
opponent, so forward steps *in*, back steps *out*, and left and right *circle him*. That is
how footwork actually reads, and it means you are thinking about distance and angle rather
than about which way is north.

---

## Three things that took the work

### Every punch was timed individually, because the obvious shortcut is wrong

Systems like this normally assume a punch lands around 40% of the way through its clip.
It is a reasonable guess and it is how most projects ship.

Measured on the real rig, across this project's clips, **the actual strike frames run from
0.149 to 0.730.** Forty percent of them are more than 0.10 away from the assumed value, and
the worst is **461 milliseconds out** — an eternity in a game decided by frames.

The interesting part is that the error is not noise. It is *systematic and backwards*: fast
inside shots peak early, around 0.15–0.26, and committed lunging shots peak late, 0.60–0.73.
A single global value is therefore wrong in **opposite directions** precisely along the
fast-versus-committed axis that the entire game is built on. It makes your quick shots feel
late and your heavy shots feel weightless, and no amount of tuning that one number fixes it,
because there is no value that is right for both.

So every clip carries its own measured strike time, found by sweeping the animation for peak
arm extension. Where a clip also carries a hand-authored impact marker, that marker wins —
it was checked against the measurement and the two disagree by a median of 7% of clip length
and by as much as 22%, which is the difference between a punch that connects and one that
arrives after the head has already moved.

### The aim rotates. It never slides.

The naive way to point a punch at a moving target is to take the gap between where the fist
is and where the target is, and add the difference to the hand's position. It works, and it
looks wrong immediately — because the hand *travels sideways*, and a punch never does that.

Here the aiming is a **rotation** solver running down chest → upper arm → forearm, turning
the chain until the fist's own axis points where it should. The punch remains the animation's
own motion, start to finish. It simply leaves on a better line. Nothing slides.

Even the axis it aims along is measured off the skeleton — wrist to middle knuckle — rather
than assumed to be some convenient world direction, so it stays correct on any rig.

### The ragdoll is mapped asymmetrically, on purpose

Hand the whole body to physics and your boxer becomes a puppet with no stance. Give physics
nothing and every landed shot is a cardboard cutout absorbing a cannonball.

So the mapping is deliberately uneven. **Hips, legs and feet stay pure animation** — footwork
is sacred and nothing is allowed to fight it. **The head is mapped lightly, the torso and arms
firmly.** The result is that a clean shot visibly rocks him — head snaps, weight shifts, the
guard breaks open — while his stance stays his own and he never stops being a boxer.

Then when he actually goes down, every muscle releases at once and the real colliders take
over completely. And if the simulation ever drifts implausibly far from where it should be,
a watchdog catches it and falls back safely rather than letting a broken rig explode into
the kind of spasming mess that ends a demo in front of judges.

---

## The opponent reacts to your punch, not to your controller

The easy way to build a hard AI is to let it read the input. It covers the instant you press,
it is unbeatable, and it feels *awful* — because it is not fighting you, it is cheating and
you can tell within thirty seconds.

This opponent writes into exactly the same input structure your gamepad does. It holds a
trigger, pushes the stick, guards, releases. The shared boxer then applies the same
preparation rules, the same stamina cost, the same fatigue curve and the same throw timing to
it that it applies to you — so the AI is not a special case. **It is playing the game.**

And its timing comes from a skill value rather than from reflexes. A threat can only be
answered inside a scheduled reaction window, which means even a highly skilled opponent
reads your punch *a beat after it has started* — the way a real fighter picks up a shoulder
drop — instead of covering before your trigger is fully held. Turn the skill down and he
reads you late and eats it. Turn it up and he is slipping shots before they land, but he is
still doing it by watching, and you can still fool him.

That is what makes the fight a conversation instead of a test.

---

## What it feels like

Distance is the whole game. Stand too close and you have no room to load a real shot. Stand
too far and your best punch falls short while his lands. So you circle, and you measure, and
you throw something cheap to see what he does with it.

Then you feel him tire. The guard drops half an inch. He works a beat slower. You have been
holding something back for this, and you step in.

**Nobody scripted that moment. It's just what happens when you make the punches real.**
