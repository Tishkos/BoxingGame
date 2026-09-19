Punch A Bunch describes its combat as physics-based and its normal controls involve winding the punch back and driving it forward with directional input rather than simply pressing a punch button.

The powerful setup I would use is:

Player Input → Punch Target → Physics Controller → Active Ragdoll → Final IK → Visual Character

Final IK becomes the finishing/procedural layer, not the physics engine.

1. What you actually need

For each boxer:

BOXER
│
├── InputController
├── PunchController
├── BalanceController
├── LocomotionController
├── PhysicsBody
│   ├── Pelvis Rigidbody
│   ├── Chest Rigidbody
│   ├── Head Rigidbody
│   ├── UpperArm L/R
│   ├── Forearm L/R
│   ├── Fist L/R
│   └── optionally physical legs
│
├── ConfigurableJoints
│
├── Animator / Reference Pose
│
├── FinalIK
│   └── FullBodyBipedIK
│
└── Hit / Damage System

Unity's ConfigurableJoint is suited to this because it gives control over joint angular limits, drives, motors, springs and damping.

2. Do NOT directly move the fist

This is probably the most important part.

Don't do:

hand.position = punchTarget.position;

And don't make Final IK directly teleport the fist into the opponent.

Instead have a physical fist Rigidbody trying to reach a desired location.

Conceptually:

             desired hand target
                    ●
                   /
                  /
 shoulder ●------● fist
          physical arm

The target says:

"I want the fist HERE."

The physics system says:

"Okay, I'll try to move there with torque/force, while obeying the elbow, shoulder, body weight, collisions and momentum."

That creates the messy but satisfying physics feel.

3. Directional punch controls

I'd make the main control work something like this.

Right punch

Hold RT.

Right stick controls the right hand.

Stick ↑        High straight
Stick ↗        High/right punch
Stick →        Wide right / hook
Stick ↘        Body hook
Stick ↓        Wind-up / low body
Stick ←        Cross-body motion

But there is another dimension:

Wind-up

Imagine the joystick controlling a virtual hand around the boxer.

You pull:

       opponent

          ↑ punch

          O fist target
          |
       boxer

stick ↓

The fist loads backward.

Then flick:

stick ↓ → ↑

and the target rapidly crosses forward.

Now you've created a punch based on the player's motion rather than:

Press X
→ play Jab.anim

That is why a skilled player can produce a much better punch than somebody button mashing.

4. Make the target exist in 3D space

Create a punch coordinate system around the chest.

Something like:

Vector3 horizontal =
    transform.right * stick.x;

Vector3 vertical =
    transform.up * stick.y;

Vector3 forward =
    transform.forward * forwardAmount;

Vector3 desired =
    shoulder.position
    + horizontal * sideRange
    + vertical * verticalRange
    + forward * reach;

But don't just map it linearly.

Use a hemisphere / punch volume:

              HIGH
               ●
          ●         ●

     ●        HEAD       ●

LEFT ●       CHEST       ● RIGHT

     ●                   ●
          ●         ●
               ●
              LOW

The hand target can move anywhere in that volume.

That alone will let the player naturally create:

jab
cross
hook
overhand
body hook
uppercut

without selecting those moves from an animation list.

5. Your “overdrive” idea

This is where it gets really interesting.

I wouldn't call it a normal charge meter.

Calculate intent velocity.

For example:

Vector2 stickVelocity =
    (currentStick - previousStick) / Time.fixedDeltaTime;

Then:

float inputSpeed = stickVelocity.magnitude;

If the player slowly moves:

0 → → →

the arm simply reaches.

If they violently flick:

BACK
 ↓

then

 ↑↑↑ FORWARD

the target enters Overdrive.

Something like:

if (
    wasWoundBack &&
    forwardVelocity > overdriveThreshold &&
    armExtension < 0.8f
)
{
    BeginPunchOverdrive();
}

During Overdrive you temporarily increase:

Arm drive force
Shoulder drive force
Chest rotation force
Hip rotation
Maximum fist target velocity

Not arbitrary damage.

Physical capability.

That's a big difference.

6. Power should NOT come directly from input

Don't do this:

damage = stickSpeed * 50;

Otherwise you have fake physics with physics visuals.

Instead input determines how aggressively the boxer attempts the movement.

The actual collision determines the result.

For example:

Vector3 relativeVelocity =
    fistRB.GetPointVelocity(contact.point)
    - opponentRB.GetPointVelocity(contact.point);

float impactSpeed =
    Mathf.Max(
        0,
        Vector3.Dot(relativeVelocity, -contact.normal)
    );

Then consider fist orientation.

float alignment =
    Mathf.Clamp01(
        Vector3.Dot(
            fistForward,
            fistRB.linearVelocity.normalized
        )
    );

So this:

perfect fist alignment
+ high velocity
+ body behind punch
= BIG hit

But this:

fast arm
+ wrist sideways
+ grazing collision
= weak/sloppy hit

That creates skill.

7. Use effective mass

This will make the combat MUCH better.

A punch shouldn't depend only on fist velocity.

Compare:

arm flailing at 10 m/s

versus:

hips + chest + shoulder + arm
driving 8 m/s

The second should be stronger.

Calculate a bodyCommitment value.

For example:

float shoulderContribution =
    Vector3.Dot(chestRB.linearVelocity, punchDirection);

float hipContribution =
    Vector3.Dot(pelvisRB.linearVelocity, punchDirection);

float handContribution =
    Vector3.Dot(fistRB.linearVelocity, punchDirection);

Then derive effective mass:

effectiveMass =
    fistMass
    + shoulderContribution * shoulderMassFactor
    + hipContribution * hipMassFactor;

An alternative is impact energy:

Energy = ½ × effectiveMass × velocity²

So speed becomes very important while body positioning still matters.

8. Rotation is critical

A good boxing game needs kinetic chaining:

feet
 ↓
hips
 ↓
chest
 ↓
shoulder
 ↓
elbow
 ↓
fist

When the right hand punches:

RIGHT CROSS

      shoulder →
   chest ↻
 hips ↻

Therefore don't drive only the arm.

Your PunchController should produce several targets:

PunchCommand
{
    handPosition;
    handRotation;

    chestRotation;
    pelvisRotation;

    shoulderBias;

    forceMultiplier;
}

The same directional input automatically changes those values.

9. Hooks happen naturally

You don't actually need:

HookAnimation

Consider the input:

wind hand outward

   boxer        fist
     O --------- ●

then rotate target inward

     O <---------●

The shoulder/chest follows.

The physical fist travels in an arc.

That's your hook.

Same for uppercut:

fist starts:

   ●
   |
 chest

then target travels:

      ↑
      ↑
      ●

while chest/pelvis drive slightly upward/forward.

10. Final IK's job

This is where your Final IK purchase becomes useful.

Final IK provides FullBodyBipedIK effectors and can position limbs while allowing influence through the rest of the body.

Use it for:

Hand targets
Left hand → physical left fist
Right hand → physical right fist
Body reaction

The punching hand can pull:

shoulder
chest
spine

slightly into the punch.

Guard

When not punching:

LHand target → left guard
RHand target → right guard
Head tracking

Head / eyes subtly follow opponent.

Foot correction

Feet remain visually planted correctly.

But Final IK itself does not turn your character into an active physics character. RootMotion specifically distinguishes Final IK as IK/procedural animation tooling from PuppetMaster, which handles character physics, animation/physics blending and ragdolls.

11. If you want the REALLY powerful setup

Since you already have Final IK, I'd seriously consider:

Final IK + PuppetMaster

PuppetMaster is RootMotion's active-ragdoll/character-physics asset. Its current Unity Asset Store version is listed as 1.5, released April 10, 2026, and compatible with Unity 6.x.

Then your architecture becomes:

Animator reference pose
        ↓
PuppetMaster physical muscles
        ↓
Physical character
        ↑
Punch forces / targets
        ↑
Directional input

Final IK
        ↓
procedural corrections

That's much closer to what you are trying to achieve.

Otherwise you need to build your own PuppetMaster-like PD active-ragdoll controller.

12. PD controller

If you're writing it yourself, this is the core of it.

Every body part wants to follow a target rotation.

Conceptually:

Torque =
    RotationError × Spring
    -
    AngularVelocity × Damping

Or:

torque =
    kp * rotationError
    - kd * rb.angularVelocity;

Where:

kp = stiffness
kd = damping

High stiffness:

strong boxer
rigid movement

Low stiffness:

loose / exhausted / stunned

This gives you another amazing mechanic.

When stunned:

muscleStrength *= 0.4f;

Suddenly he physically becomes wobbly.

No special "stunned animation" required.

13. Taking a punch

Opponent gets hit at:

       HEAD
        ● ← FIST
        |
      chest
        |
      pelvis

Use:

opponentRB.AddForceAtPosition(
    punchDirection * impulse,
    contactPoint,
    ForceMode.Impulse
);

Force at the actual contact location generates rotational motion naturally.

Hit left side of jaw:

head rotates right

Hit forehead:

head goes backward

Body shot:

torso folds / moves

This is far better than:

animator.Play("HitReact01");

You can still blend an animation underneath it.

14. Guard should also be physics

Don't create a magic:

if (blocking)
    damage = 0;

The glove should physically intercept the punch.

So:

ATTACKER      DEFENDER

 fist → →   ● glove
              \
               head

The attacking fist collides with the defending glove.

Then physics decides where the energy goes.

You can add gameplay logic for:

successful block
parry
guard damage
stamina

but the collision itself should still happen physically.

Punch A Bunch's controls also use directional blocking, according to player descriptions: players hold the block input and direct the stick toward the incoming punch.

15. Balance

You absolutely need a separate:

BalanceController

Otherwise your active ragdoll will constantly fall down.

Calculate center of mass:

                COM
                 ●
                 |
           ┌──────────┐
          leftFoot rightFoot

The boxer should attempt to keep COM over the support region.

If:

COM exits support area

increase corrective torque / step.

If the hit is too powerful:

correction can't compensate
        ↓
stumble
        ↓
fall

Now knockdowns happen naturally.

16. Feet

Don't make the legs completely floppy.

I would use:

pelvis = highly controlled
legs = strongly controlled
upper body = more physical
arms = very physical
head = physical

Approximately:

Pelvis     90% control
Legs       85%
Chest      70%
Head       50%
Arms       60%
Fists      physics-heavy

When knocked out:

all muscle strength → 0.05

and the fighter collapses.

17. Important Rigidbody settings

For fast gloves use continuous collision detection so they don't tunnel through heads/gloves.

And for the main boxer Rigidbody, interpolation can smooth motion between fixed physics updates; Unity recommends interpolation for player objects/camera-followed physics objects when jitter is visible.

I'd start around:

Fixed Timestep
0.01

which is:

100 physics Hz

Boxing benefits hugely from a higher physics rate.

I'd experiment with:

60 Hz
100 Hz
120 Hz

rather than starting at Unity's common 50 Hz.

18. Collider setup

Don't use one giant capsule for combat.

Something like:

            Head
           (sphere)

        upper chest
         capsule

 arm                 arm
capsule             capsule

 forearm            forearm
 capsule            capsule

 [GLOVE]            [GLOVE]
 box/capsule        box/capsule

           pelvis
          capsule

Use separate hit zones:

Jaw
Temple
Forehead
Body
Liver
Solar plexus
Guard

But damage should still originate from the physical collision.

19. Your punch state machine

Keep the punch logic simple.

GUARD
  ↓
WINDUP
  ↓
ACCELERATE
  ↓
IMPACT / MISS
  ↓
RECOVER
  ↓
GUARD

Not:

JabState
HookState
UppercutState
CrossState
OverhandState
...

The trajectory defines the punch type.

You can classify it afterward if you need commentary/UI:

if (verticalChange > threshold)
    type = Uppercut;

else if (horizontalArc > threshold)
    type = Hook;

else
    type = Straight;

That's a massive difference.

20. The complete system

This is what I would build:

                 GAMEPAD
                    │
        right-stick trajectory
                    │
                    ▼
            PunchInputAnalyzer
                    │
        ┌───────────┼───────────┐
        │           │           │
     direction    speed       windup
        │           │           │
        └───────────┼───────────┘
                    ▼
             PunchTrajectory
                    │
        ┌───────────┴────────────┐
        ▼                        ▼
 Hand Physics Target       Body Torque Target
        │                        │
        ▼                        ▼
  Arm PD Controller       Torso PD Controller
        │                        │
        └───────────┬────────────┘
                    ▼
             ACTIVE RAGDOLL
                    │
                    ▼
                 COLLISION
                    │
       ┌────────────┼─────────────┐
       ▼            ▼             ▼
 velocity      effective mass   angle
       │            │             │
       └────────────┼─────────────┘
                    ▼
               IMPACT ENERGY
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
       damage             physical impulse
                              │
                              ▼
                      opponent ragdoll
What I would personally use for your project

Keep Final IK.

Then use:

Unity Rigidbody/PhysX
ConfigurableJoint
compound body colliders
physical fists
PD muscle controllers
directional stick punch target
punch trajectory analyser
impact-energy calculation
physical blocking
center-of-mass balance controller
Final IK FullBodyBipedIK for visual/procedural correction
optionally PuppetMaster instead of writing the entire active-ragdoll layer yourself

And importantly:

No predefined jab/hook/uppercut animations as the actual attack.

You can have a subtle idle/boxing animation underneath, but the punch itself should emerge from:

direction + wind-up + velocity + body rotation + physics.

That is the system that would give you the “difficult at first, insanely satisfying once mastered” feeling that makes Punch A Bunch interesting rather than just another boxing animation system.

do i need actuall animationions ? 

Not for the actual punches, if you build the system the way I described.

You’ll still want some animations, but mainly as a base/reference layer:

idle boxing stance
breathing
basic footwork / walking
guard pose
maybe dodge/lean reference poses
get-up animation after a knockdown

For the punches themselves, you can make them almost entirely procedural/physical:

stick direction → hand target → shoulder/chest/hip rotation → physical joints → collision

So you don’t need separate:

Jab.anim
Cross.anim
LeftHook.anim
RightHook.anim
Uppercut.anim

The player’s movement creates those punches naturally.

For example, if the right-hand target moves straight forward, it becomes a straight. If it travels outward and then arcs inward, it becomes a hook. If it starts low and accelerates upward, it becomes an uppercut.

I’d use animation more like a muscle/reference pose, while the physics overrides it:

Idle boxing animation
        ↓
Reference skeleton
        ↓
Active-ragdoll / physical joints
        ↓
Directional punch controller
        ↓
Final IK corrections
        ↓
Visible boxer

That also means getting hit doesn’t necessarily need a library of hit animations. A punch to the left side of the jaw can physically rotate the head and torso, and a powerful body shot can physically push or fold the fighter.

So for your game, I’d say roughly 20% traditional animation, 80% procedural/physics.

The one area I would definitely keep animated is locomotion. Fully physics-driven walking is much harder to make responsive and can easily look drunk or floppy. Use proper walk/step/stance animations for the legs, then let the upper body and punches be much more physically driven.

be more detailed about pucnhing system please much more detailed more powerfull fun to play

Yes. If you want this to be the core mechanic of the game, I would go much deeper than simply moving an IK target with the stick.

The goal should be:

The player controls the intention and trajectory of the punch. The character's body physically attempts to execute it. Physics determines whether it was actually a good punch.

That gives you something that can be easy to understand but difficult to master.

1. The core idea

Do not have controls like:

X = Jab
Y = Hook
B = Uppercut

Instead:

LEFT TRIGGER  = control left fist
RIGHT TRIGGER = control right fist

RIGHT STICK / MOUSE
        ↓
direction of hand movement
        ↓
punch trajectory
        ↓
body automatically follows
        ↓
physics determines impact

Imagine your right fist has an invisible 3D control volume around your body:

                   HIGH

                    ●
               ●         ●

          ●                    ●

 LEFT      ●      HEAD       ●      RIGHT

          ●       CHEST        ●

               ●         ●
                    ●

                   LOW


             YOUR CHARACTER

The stick doesn't select an attack.

It moves a desired hand target inside this space.

That target is then chased by the physical arm.

2. Separate three things

This is extremely important.

You should have:

PLAYER INTENT
     ↓
VIRTUAL PUNCH TARGET
     ↓
PHYSICAL BODY

The virtual target can move very quickly.

The real fist cannot instantly reach it.

Example:

target
                      X
                     /
                    /
shoulder ●----------● fist

The controller says:

Get the fist toward X.

But the arm has:

mass
joint limits
shoulder strength
elbow strength
momentum
collisions
body position
stamina
balance

Therefore sometimes the fist reaches the target beautifully.

Sometimes it doesn't.

That's where your gameplay comes from.

3. Don't let Final IK drive the punch physics

Final IK should help the visual skeleton.

The hierarchy I recommend is:

INPUT
 ↓
Punch Intent
 ↓
Procedural Target Generator
 ↓
Active Ragdoll / Rigidbody Controller
 ↓
Physical Skeleton
 ↓
Final IK / Visual Correction
 ↓
Mesh

Unity's ConfigurableJoint is particularly useful here because it lets you constrain angular movement and drive joints toward desired rotations; targetRotation can be used as the desired rotation for the joint.

Final IK then fixes visual problems such as hand placement, elbow alignment, feet, head tracking, etc. RootMotion itself describes Final IK as a collection of IK solvers/tools rather than a full character-physics solution.

4. Give every arm a virtual controller

For each hand:

PunchHandController
{
    Transform shoulder;
    Rigidbody upperArm;
    Rigidbody forearm;
    Rigidbody fist;

    Vector3 desiredPosition;
    Quaternion desiredRotation;

    Vector3 targetVelocity;
    Vector3 targetAcceleration;

    float extension;
    float powerIntent;
}

The important values aren't just:

position

but:

position
velocity
acceleration
rotation

Because:

A target moving slowly forward should make the fighter reach.

A target exploding forward should make them punch.

5. Use stick velocity, not just stick position

Suppose the player does:

stick:

↓ ↓ ↓

slowly.

That's a wind-up.

Then suddenly:

↑↑↑↑↑

That's acceleration.

Calculate:

Vector2 stickVelocity =
    (stickCurrent - stickPrevious)
    / Time.fixedDeltaTime;

Then:

Vector2 stickAcceleration =
    (stickVelocity - previousStickVelocity)
    / Time.fixedDeltaTime;

Now you know whether the player:

slowly moved
flicked
changed direction
made a circular movement
hesitated

That makes input expressive.

6. The punch should have a wind-up

This is where it becomes fun.

Imagine neutral guard:

Opponent

      👊
       \
        O
       / \

The player pulls the stick backward.

Their right hand shifts slightly backward:

                   opponent
                       ↑

                O
                 \
                  👊  ← loaded

The shoulder also opens slightly.

Chest begins rotating.

Hip stores rotation.

Then the player snaps forward.

hip
  ↻
chest
    ↻
shoulder
       →
elbow
           →
fist
                →→→→

That gives you a kinetic chain.

7. Don't make wind-up ridiculously exaggerated

You want a game, not Octodad boxing.

Limit hand movement.

Maybe the normal guard hand can move:

Backward:     0.25 m
Forward:      arm reach
Sideways:     0.35 m
Vertical:     0.40 m

The actual numbers depend on your character.

The player's input determines a position inside that allowed volume.

8. Automatically generate body contribution

This is where I would make your system feel much more sophisticated.

The player controls primarily the fist.

Your system derives:

elbow
shoulder
chest
spine
hips
weight shift

from the fist trajectory.

For instance, determine punch direction:

Vector3 punchDirection =
    desiredHandVelocity.normalized;

Then calculate how much torso involvement is appropriate:

float forwardIntent =
    Vector3.Dot(
        punchDirection,
        characterForward
    );

float lateralIntent =
    Vector3.Dot(
        punchDirection,
        characterRight
    );

float verticalIntent =
    Vector3.Dot(
        punchDirection,
        characterUp
    );

Now you can infer body mechanics.

9. Straight punch

If:

forwardIntent = high
lateralIntent = low
verticalIntent = low

the system recognizes a straight trajectory.

Then automatically produce:

small hip rotation
moderate chest rotation
shoulder forward
elbow behind fist
slight weight transfer

Not an animation.

A biomechanical response.

10. Hook

Suppose the fist moves:

OUTWARD
   →
      ↘
        ↘

then

         ← ← ← inward

Your system detects high lateral velocity plus angular movement around the chest.

Now torso rotation becomes stronger.

              opponent

                  O
               ↙

      fist ● ← ←
            \
             shoulder

               ↻ chest
             ↻ hips

The hand travels naturally in an arc.

You never explicitly told it:

PlayHook();

You simply let the trajectory create a hook.

11. Uppercut

Input:

pull fist low

        O
        |
       👊

then

        ↑
        ↑
        👊
        O

The controller detects:

high positive vertical velocity
forward velocity
hand previously below chest

Then generate:

slight knee/hip drive
chest extension
shoulder rise
elbow underneath fist

Now it becomes an uppercut.

12. Overhand

This can emerge naturally too.

Fist:

       ●
       ↑ load high

then

       ↘
         ↘
           ↘

             TARGET

The system detects:

starting above shoulder
forward + downward acceleration
strong torso rotation

and responds with an overhand-style body contribution.

Again:

no Overhand animation necessary.

13. Now create your "Overdrive" system

This could make your game especially satisfying.

Don't make Overdrive simply:

press button
damage x2

Instead Overdrive means:

The player's motion qualifies for temporarily increased muscular output.

For example:

float overdrive =
    windupQuality *
    directionalChange *
    inputAcceleration *
    bodyAlignment *
    balance;

Imagine these values:

Good windup               0.95
Fast directional reversal 0.90
Body balanced             0.92
Correct trajectory        0.88
Good shoulder alignment   0.91

Result:

OVERDRIVE = 0.90

Now temporarily increase physical joint drive.

Not damage.

Something like:

armStrength =
    baseArmStrength *
    Mathf.Lerp(1f, 1.45f, overdrive);

Same with:

chest torque
hip torque
shoulder drive
target velocity

The punch actually becomes physically faster and heavier.

That feels much more legitimate.

14. Have three punch phases

Internally I'd use something like:

LOAD
  ↓
DRIVE
  ↓
RECOVERY

Rather than hundreds of animation states.

Load

The fist moves opposite or sideways from intended strike direction.

Muscles remain controlled.

Torso prepares.

Drive

Large target acceleration is detected.

Muscle output increases.

Kinetic chain activates.

Recovery

After maximum extension or impact:

power rapidly drops
hand target returns toward guard
torso stabilizes
balance controller takes priority

Recovery becomes gameplay.

15. Bad punches should actually be bad

This is critical.

If every random stick flick produces a devastating punch, the mechanic will get boring quickly.

Example:

Player does:

random crazy stick movement
↙↑→↓↗←

The character should start throwing ugly arm punches.

You calculate a PunchQuality:

quality =
    trajectoryQuality *
    wristAlignment *
    elbowAlignment *
    bodyContribution *
    balanceQuality *
    extensionQuality;

A beautiful punch:

trajectory       0.94
wrist            0.98
elbow            0.90
body             0.92
balance          0.95

quality ≈ excellent

A wild punch:

trajectory       0.70
wrist            0.45
elbow            0.55
body             0.28
balance          0.40

It might still hit.

But it will have bad effective mass.

16. Don't calculate damage from PunchQuality

Important distinction.

Don't say:

damage = punchQuality * 100;

Instead quality affects how effectively the fighter generates physics.

The actual collision then determines damage.

Meaning:

INPUT
 ↓
technique
 ↓
body produces velocity/mass
 ↓
COLLISION
 ↓
actual damage

Much better.

17. Calculate real collision velocity

At impact:

Vector3 relativeVelocity =
    fistRb.GetPointVelocity(contactPoint)
    -
    targetRb.GetPointVelocity(contactPoint);

Then calculate velocity into the collision plane.

float impactVelocity =
    Mathf.Max(
        0f,
        Vector3.Dot(
            relativeVelocity,
            -contactNormal
        )
    );

Now a grazing hit:

fist
→→→→

      head
       O

barely touching side

is weaker than:

fist →→→ O
           head
18. Fist orientation matters

This is one feature I would absolutely add.

You want the knuckles aligned with the punch direction.

Calculate:

float wristAlignment =
    Vector3.Dot(
        fistForward,
        fistVelocity.normalized
    );

Then clamp it.

Good:

velocity →
fist     →

Bad:

velocity →
fist     ↑

Bad wrist alignment reduces effective transfer.

Potentially it could also hurt the attacker's hand later if you want deeper simulation.

19. Effective mass

This is one of the biggest upgrades you can make.

Don't treat the fist's Rigidbody mass as the entire punch.

A real punch can involve:

fist
forearm
upper arm
shoulder
torso
hips
forward body movement

So estimate effective mass.

For example:

float effectiveMass =
    fistMass;

effectiveMass +=
    forearmContribution * 1.5f;

effectiveMass +=
    upperArmContribution * 2.0f;

effectiveMass +=
    chestContribution * 5.0f;

effectiveMass +=
    hipContribution * 7.0f;

Those numbers are examples, not real physical measurements.

Then:

float energy =
    0.5f *
    effectiveMass *
    impactVelocity *
    impactVelocity;

Now:

FAST ARM FLAIL

fist = 10 m/s
body = doing nothing

effective mass = low

might be less damaging than:

CLEAN CROSS

fist = 8 m/s
hips = driving
chest = rotating
shoulder = behind punch

effective mass = high

That's exactly what you want.

20. Add a kinetic-chain score

I'd actually calculate whether energy arrived in the right sequence.

You track:

hip angular velocity
chest angular velocity
shoulder velocity
elbow velocity
fist velocity

For a strong right cross:

HIP
starts moving

~30 ms later
CHEST

~30 ms later
SHOULDER

~30 ms later
ARM

then
FIST

You don't need scientifically perfect values.

You're creating satisfying game physics.

Call this:

float kineticChainQuality;

Excellent sequencing increases how much of the body's mass contributes.

21. Full extension sweet spot

There should be a distance where the punch is strongest.

Too close:

👊O

The fighter is jammed.

Not enough acceleration.

Too far:

👊--------O

The arm overextends before impact.

Sweet spot:

👊------O
       ↑
     impact

Calculate:

float extension =
    distanceShoulderToFist /
    maximumArmLength;

Maybe ideal impact:

0.75 – 0.95

not necessarily exactly 1.0.

Now distance management matters.

22. That's how you make footwork important

If the opponent is 20 cm too far away, you can't magically extend the hitbox.

You need:

step
lean
rotate
close distance

Otherwise:

WHIFF

And whiffing should be dangerous.

23. Whiffs should create momentum

Suppose the player puts huge power into a hook:

      ↻↻↻

👊 ← ← ←

but misses.

Don't immediately return them safely to guard.

Their fist has momentum.

Their torso has rotational momentum.

They should physically overshoot slightly.

BIG HOOK

                 opponent
                    O

          👊 →
        /
       O
        \
         body rotated too far

Now the opponent gets a counter opportunity.

This creates strategy without artificial cooldowns.

24. Recovery becomes the cooldown

Rather than:

punchCooldown = 0.5f;

your cooldown is physical.

After a huge punch:

fist far from guard
torso rotated
weight shifted
balance compromised

The fighter physically needs to recover.

A small jab:

fast recovery

Huge overhand:

slow recovery

That's much better game design.

25. Guard should use exactly the same hands

There shouldn't be separate:

PunchCollider
BlockCollider

The gloves are always physical.

When the hand is near guard:

       👊 👊
        \ /
         O

and the opponent hits:

attacker → 👊 [DEFENDER GLOVE] O

the attacking glove hits the defending glove.

Physics decides what happens.

26. But add guard strength

Physical collisions alone probably won't give you sufficiently predictable game feel.

So add a physical GuardStrength.

When guarding:

arm muscle drive = stronger
shoulder stability = stronger
chest stabilisation = stronger

When tired:

guardStrength ↓

Then a massive punch physically pushes their glove into their face.

That would look great:

attacker

👊 →→→ [👊] → O
             defender

You could give reduced damage from the glove being pushed backward.

27. Parrying

Now you can have natural parries.

The defender moves their glove toward the incoming punch:

incoming
→→→ 👊

      ↑ defending glove
      👊

Contact occurs sideways.

Instead of absorbing all energy, it redirects the attacking fist.

Because you're using rigidbody physics, the fist trajectory can actually be knocked away.

That is far more satisfying than:

PARRY!
animation triggered
28. Head movement

This can use similar controls.

Maybe:

Left stick = movement

Right stick = punching while trigger held

Right stick without punch trigger = upper-body/head movement

Then:

← slip left
→ slip right
↓ duck
↙ weave
↘ weave

But I'd constrain the movement procedurally.

Don't literally move the head wherever the stick says.

Instead set:

head target
chest lean
pelvis compensation

Your balance controller then decides how far the boxer can safely move.

29. Knockback should come from contact position

When hitting the opponent, applying force at the actual contact point gives both force and torque in Unity. That's exactly why Rigidbody.AddForceAtPosition is useful here.

Example:

Hit center forehead:

      👊 → O
            \
             body moves back

Hit jaw side:

             ↻
👊 →        O

Hit upper torso:

👊 →     CHEST
           |
         pelvis

different reaction.

You shouldn't choose:

HitReaction01
HitReaction02
HitReaction03

Physics gives you enormous variation for free.

30. Local damage zones

I'd have colliders for:

Zone	Effect
Forehead	resistant, mainly head displacement
Jaw	high stun/rotation potential
Temple	high stun potential
Nose/face	moderate damage
Upper chest	moderate
Solar plexus	stamina disruption
Liver	high body damage
Ribs	body damage
Arms/gloves	block / reduced damage

But don't simply create arbitrary multipliers like:

jaw = x5 damage

I'd make some zones affect different systems.

For example:

JAW HIT
→ head angular acceleration
→ concussion meter

BODY HIT
→ stamina
→ breathing/recovery

ARM HIT
→ temporary arm strength

Much richer.

31. Stun should be physical

Have a MotorStrength for the active ragdoll.

Normal:

Leg strength     1.0
Pelvis           1.0
Spine            1.0
Neck             0.9
Arms             1.0

After big jaw shot:

Leg strength     0.70
Pelvis           0.65
Spine            0.55
Neck             0.40
Arms             0.65

for perhaps 0.2–1.5 seconds depending on impact.

Suddenly the player becomes:

wobbly
loose
harder to control

without a "stunned animation."

That would be fantastic visually.

32. Don't completely remove player control when stunned

This matters for fun.

Bad:

You got stunned.
Controls disabled for 3 seconds.

Frustrating.

Better:

Your fighter's muscles are weaker.
You STILL control them.

So the player desperately tries:

guard
back away
regain balance
cling to opponent

while their character physically struggles.

That's emergent gameplay.

33. Balance controller

This system is almost as important as punching.

Calculate combined center of mass from all Rigidbodies:

Vector3 com;
float totalMass = 0f;

foreach (var rb in bodies)
{
    com += rb.worldCenterOfMass * rb.mass;
    totalMass += rb.mass;
}

com /= totalMass;

Now compare projected COM to feet.

         COM
          ●
          |
          |
     👟-------👟
       support

Safe:

         ●
      [------]

Dangerous:

              ●
      [------]

If outside support:

balance ↓

The controller tries to correct using:

pelvis torque
spine torque
foot stepping
34. Powerful punches should sacrifice balance

Suppose the player throws a huge cross.

They get:

+ fist velocity
+ effective mass
+ body rotation

but:

- defensive readiness
- balance
- recovery speed

That creates a real risk/reward system.

Small jab:

POWER       ██
SPEED       █████
RECOVERY    █████
BALANCE     █████

Huge overhand:

POWER       █████
SPEED       ████
RECOVERY    ██
BALANCE     ██

And again, those differences largely emerge from physics instead of attack definitions.

35. Give the player subtle assistance

Pure physics can feel awful.

This is important.

You want:

physics-assisted controls

not:

physics simulator controls

The game should quietly help the player.

For example, when a forward punch is detected:

Auto-correct elbow behind fist       40%
Auto-align wrist                     35%
Auto-add shoulder rotation           50%
Auto-add hip rotation                35%
Auto-maintain balance                70%

A beginner therefore produces decent punches.

But an expert who creates good input trajectories gets much stronger results.

This is similar to driving games.

You aren't manually controlling:

clutch pressure
brake cylinder pressure
steering rack
differential

Yet there's deep physics underneath.

Same concept.

36. Create an invisible "boxing intelligence"

I would literally create:

BoxingMotor

Its responsibility:

Player says:
"I want my right fist going here this fast."

BoxingMotor determines:
"How should a human-ish boxer attempt that?"

It controls:

elbow
shoulder
clavicle
spine
hips
weight shift
guard hand
balance

This allows physically driven combat without making the controls impossible.

37. The other hand should react

This will make punches look dramatically better.

If throwing right cross:

RIGHT
→ punches

LEFT
→ stays near jaw
→ slightly counterbalances

CHIN
→ tucks

LEFT SHOULDER
→ stays defensive

If throwing left hook:

right hand maintains guard

Unless the player is actively controlling both.

This makes the character appear trained.

38. Allow advanced players to override assistance

Later you could have:

Simple controls
Advanced controls

Simple:

one stick intelligently drives punches

Advanced:

Left stick       movement
Right stick      trajectory
LT               left hand
RT               right hand
LB/RB modifiers

Eventually players could discover crazy combinations.

39. Simultaneous punching

Because you're not animation-state based, both fighters—and even both hands—can potentially interact at the same time.

Example:

LEFT HOOK        RIGHT CROSS
      ↘            ↙
       👊        👊
         \      /
          O    O

There doesn't need to be a special "trade punches" animation.

Both physical fists collide with heads.

Calculate each collision independently.

That's fantastic for boxing.

40. Fist-to-fist collisions

Keep them.

Imagine:

player punch   → 👊👊 ← opponent punch

The gloves physically collide.

Their momentum affects both arms.

This creates accidental:

blocks
hand fighting
punch deflections
clashes

that you never animated.

That is exactly the type of emergent interaction you want.

Because gloves are fast-moving bodies, consider Unity's continuous collision detection to prevent them tunneling through targets. Unity specifically recommends ContinuousDynamic for selected fast-moving Rigidbodies, while noting the higher physics cost.

41. Don't let the fist teleport

Your virtual target might jump:

TARGET

A -------------------------- B

But actual desired physical target velocity should be limited:

desiredVelocity =
    Vector3.ClampMagnitude(
        targetDelta / Time.fixedDeltaTime,
        maxHandTargetVelocity
    );

And acceleration should be limited too.

Otherwise you're effectively injecting infinite energy into PhysX.

42. Physical hand motor

You can use a PD-style controller:

Vector3 positionError =
    desiredPosition - fist.position;

Vector3 velocityError =
    desiredVelocity - fist.linearVelocity;

Vector3 force =
    positionError * positionGain
    +
    velocityError * velocityGain;

Then:

fist.AddForce(force, ForceMode.Force);

However, don't make the fist completely independent of the arm.

Ideally most of the power should be generated through joint drives and torso movement.

Direct fist force should primarily act as assistance.

Otherwise you'll get:

magical rocket fist

where the hand drags the whole body.

43. Shoulder is the main engine

For punching I'd roughly think:

HAND TARGET
      ↓
Elbow decides shape
      ↓
Shoulder attempts trajectory
      ↓
Chest contributes rotation
      ↓
Pelvis contributes rotation
      ↓
Feet maintain base

The shoulder/chest should generate the majority of motion.

The fist target tells them where you're trying to go.

44. Elbow solver

This needs special attention.

Given:

shoulder
fist target

you need to determine sensible elbow orientation.

For straight:

shoulder -------- elbow -------- fist

For hook:

             elbow
               ●
              /
shoulder ●    /
          \  /
           fist

For uppercut:

shoulder
   ●
    \
     elbow
       ●
       |
       ● fist

Final IK can help tremendously here.

But your procedural system should provide an elbow hint/pole target.

45. Wrist rotation

Automatically rotate fist based on trajectory.

Straight:

load:
vertical-ish fist

drive:
gradually pronates

impact:
knuckles aligned

Hook:

slightly different fist orientation

Uppercut:

palm orientation more upward/inward

Again, subtly assist.

Don't make the player manually rotate the wrist unless you're making an insane boxing simulator. 😂

46. Add haptic feedback

This mechanic would benefit enormously from controller vibration.

Small glove contact:

tiny pulse

Clean face connection:

sharp strong pulse

Heavy blocked punch:

strong but dull pulse

Huge counter:

very sharp pulse

This makes physical interactions feel far more powerful.

47. Audio should reflect physics too

Don't randomly choose punch sounds.

Use:

impact energy
surface
hit location
alignment

to choose/mix sounds.

For example:

glancing jaw:
"tap/thud"

clean jaw:
"CRACK"

glove block:
"PUFF"

body:
deep "THUMP"

And scale pitch/volume somewhat using impact speed.

48. Camera response should be extremely controlled

Don't shake for every hit.

For a huge hit:

2–5 ms visual freeze / impact pause
small camera impulse
subtle FOV response
controller vibration
head reaction
sound

A tiny amount can make a physics hit feel 10× stronger.

Do not overdo it or you'll destroy the physical feeling.

49. Add hit-stop carefully

This sounds contradictory with physics, but you can fake a tiny impact emphasis.

For extremely strong punches:

0.02–0.05 sec

visual/time response.

Not:

0.3 second anime freeze

For a boxing game, subtlety is better.

50. The gameplay loop becomes beautiful

Eventually the player is thinking:

He's far away.

step in
↓
jab
↓
he slips left
↓
pull right hand back
↓
rotate hips
↓
throw right hook
↓
he blocks
↓
my glove gets deflected
↓
I'm off balance
↓
he counters
↓
I desperately raise left glove
↓
partial block
↓
back away

And very little of that needs predefined attack animation logic.

That's the power of this architecture.

The architecture I'd actually build
┌────────────────────────────┐
│       PLAYER INPUT         │
│ stick / mouse / triggers   │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│      INPUT ANALYZER        │
│ position                   │
│ velocity                   │
│ acceleration               │
│ circular motion            │
│ reversal                   │
│ wind-up                    │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│     PUNCH INTENTION        │
│ hand target                │
│ target velocity            │
│ target rotation            │
│ desired power              │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│       BOXING MOTOR         │
│ elbow solver               │
│ shoulder contribution      │
│ chest rotation             │
│ hip rotation               │
│ weight transfer            │
│ wrist alignment            │
│ opposite-hand guard        │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│      ACTIVE RAGDOLL        │
│ Rigidbody                  │
│ ConfigurableJoint          │
│ PD muscle motors           │
│ joint limits               │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│          PHYSICS           │
│ momentum                   │
│ collisions                 │
│ blocking                   │
│ deflections                │
│ balance                    │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│       HIT ANALYZER         │
│ relative velocity          │
│ effective mass             │
│ impact angle               │
│ wrist alignment            │
│ target location            │
└─────────────┬──────────────┘
              ↓
┌────────────────────────────┐
│       DAMAGE / STUN        │
│ head trauma                │
│ body damage                │
│ stamina                    │
│ muscle weakness            │
│ knockdown                  │
└────────────────────────────┘

And running beside all of that:

BalanceController
FinalIKController
LocomotionController
StaminaController
CameraFeedback
AudioFeedback
Haptics
The secret to making it FUN

The system shouldn't be 100% realistic.

I would aim for roughly:

60% believable physics
25% invisible player assistance
15% exaggerated game feel

Pure simulation will probably feel slow, floppy and frustrating.

Pure animation will feel predictable.

The sweet spot is:

The player believes physics caused what happened, even though you're secretly helping physics produce something fun.

That's what I would build for your game.

And if you're building this in Unity now, I'd start with one boxer standing still, one physical right arm, one dummy opponent and one right-stick punch target. Get just straight/hook/uppercut trajectories feeling amazing before adding locomotion, AI, damage or an entire active ragdoll. Once that one arm feels addictive, the rest of the boxing game can grow around it.