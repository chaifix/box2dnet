// ****************************************************************************
// Copyright (c) 2011, Daniel Murphy
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
// * Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// * Redistributions in binary form must reproduce the above copyright notice,
// this list of conditions and the following disclaimer in the documentation
// and/or other materials provided with the distribution.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
// IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
// INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
// NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
// PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
// ****************************************************************************

using System;
using System.Diagnostics;
using Box2D.Common;
using Box2D.Pooling;

namespace Box2D.Dynamics.Joints
{

    //Point-to-point constraint
    //C = p2 - p1
    //Cdot = v2 - v1
    //   = v2 + cross(w2, r2) - v1 - cross(w1, r1)
    //J = [-I -r1_skew I r2_skew ]
    //Identity used:
    //w k % (rx i + ry j) = w * (-ry i + rx j)

    //Motor constraint
    //Cdot = w2 - w1
    //J = [0 0 -1 0 0 1]
    //K = invI1 + invI2

    /// <summary>
    /// A revolute joint constrains two bodies to share a common point while they are free to rotate
    /// about the point. The relative rotation about the shared point is the joint angle. You can limit
    /// the relative rotation with a joint limit that specifies a lower and upper angle. You can use a
    /// motor to drive the relative rotation about the shared point. A maximum motor torque is provided
    /// so that infinite forces are not generated.
    /// </summary>
    /// <author>Daniel Murphy</author>
    public class RevoluteJoint : Joint
    {
        // Solver shared
        public readonly Vec2 m_localAnchorA = new Vec2();
        public readonly Vec2 m_localAnchorB = new Vec2();
        public readonly Vec3 m_impulse = new Vec3();
        public float m_motorImpulse;

        public bool m_enableMotor;
        public float m_maxMotorTorque;
        public float m_motorSpeed;

        public bool m_enableLimit;
        public float m_referenceAngle;
        public float m_lowerAngle;
        public float m_upperAngle;

        // Solver temp
        public int m_indexA;
        public int m_indexB;
        public readonly Vec2 m_rA = new Vec2();
        public readonly Vec2 m_rB = new Vec2();
        public readonly Vec2 m_localCenterA = new Vec2();
        public readonly Vec2 m_localCenterB = new Vec2();
        public float m_invMassA;
        public float m_invMassB;
        public float m_invIA;
        public float m_invIB;
        public readonly Mat33 m_mass = new Mat33(); // effective mass for point-to-point constraint.
        public float m_motorMass; // effective mass for motor/limit angular constraint.
        public LimitState m_limitState;

        public RevoluteJoint(IWorldPool argWorld, RevoluteJointDef def)
            : base(argWorld, def)
        {
            m_localAnchorA.Set(def.localAnchorA);
            m_localAnchorB.Set(def.localAnchorB);
            m_referenceAngle = def.referenceAngle;

            m_motorImpulse = 0;

            m_lowerAngle = def.lowerAngle;
            m_upperAngle = def.upperAngle;
            m_maxMotorTorque = def.maxMotorTorque;
            m_motorSpeed = def.motorSpeed;
            m_enableLimit = def.enableLimit;
            m_enableMotor = def.enableMotor;
            m_limitState = LimitState.INACTIVE;
        }

        public override void InitVelocityConstraints(SolverData data)
        {
            m_indexA = BodyA.IslandIndex;
            m_indexB = BodyB.IslandIndex;
            m_localCenterA.Set(BodyA.Sweep.LocalCenter);
            m_localCenterB.Set(BodyB.Sweep.LocalCenter);
            m_invMassA = BodyA.InvMass;
            m_invMassB = BodyB.InvMass;
            m_invIA = BodyA.InvI;
            m_invIB = BodyB.InvI;

            // Vec2 cA = data.positions[m_indexA].c;
            float aA = data.Positions[m_indexA].A;
            Vec2 vA = data.Velocities[m_indexA].V;
            float wA = data.Velocities[m_indexA].W;

            // Vec2 cB = data.positions[m_indexB].c;
            float aB = data.Positions[m_indexB].A;
            Vec2 vB = data.Velocities[m_indexB].V;
            float wB = data.Velocities[m_indexB].W;

            Rot qA = Pool.PopRot();
            Rot qB = Pool.PopRot();
            Vec2 temp = Pool.PopVec2();

            qA.Set(aA);
            qB.Set(aB);

            // Compute the effective masses.
            Rot.MulToOutUnsafe(qA, temp.Set(m_localAnchorA).SubLocal(m_localCenterA), m_rA);
            Rot.MulToOutUnsafe(qB, temp.Set(m_localAnchorB).SubLocal(m_localCenterB), m_rB);

            // J = [-I -r1_skew I r2_skew]
            // [ 0 -1 0 1]
            // r_skew = [-ry; rx]

            // Matlab
            // K = [ mA+r1y^2*iA+mB+r2y^2*iB, -r1y*iA*r1x-r2y*iB*r2x, -r1y*iA-r2y*iB]
            // [ -r1y*iA*r1x-r2y*iB*r2x, mA+r1x^2*iA+mB+r2x^2*iB, r1x*iA+r2x*iB]
            // [ -r1y*iA-r2y*iB, r1x*iA+r2x*iB, iA+iB]

            float mA = m_invMassA, mB = m_invMassB;
            float iA = m_invIA, iB = m_invIB;

            bool fixedRotation = (iA + iB == 0.0f);

            m_mass.Ex.X = mA + mB + m_rA.Y * m_rA.Y * iA + m_rB.Y * m_rB.Y * iB;
            m_mass.Ey.X = (-m_rA.Y) * m_rA.X * iA - m_rB.Y * m_rB.X * iB;
            m_mass.Ez.X = (-m_rA.Y) * iA - m_rB.Y * iB;
            m_mass.Ex.Y = m_mass.Ey.X;
            m_mass.Ey.Y = mA + mB + m_rA.X * m_rA.X * iA + m_rB.X * m_rB.X * iB;
            m_mass.Ez.Y = m_rA.X * iA + m_rB.X * iB;
            m_mass.Ex.Z = m_mass.Ez.X;
            m_mass.Ey.Z = m_mass.Ez.Y;
            m_mass.Ez.Z = iA + iB;

            m_motorMass = iA + iB;
            if (m_motorMass > 0.0f)
            {
                m_motorMass = 1.0f / m_motorMass;
            }

            if (m_enableMotor == false || fixedRotation)
            {
                m_motorImpulse = 0.0f;
            }

            if (m_enableLimit && fixedRotation == false)
            {
                float jointAngle = aB - aA - m_referenceAngle;
                if (MathUtils.Abs(m_upperAngle - m_lowerAngle) < 2.0f * Settings.ANGULAR_SLOP)
                {
                    m_limitState = LimitState.EQUAL;
                }
                else if (jointAngle <= m_lowerAngle)
                {
                    if (m_limitState != LimitState.AT_LOWER)
                    {
                        m_impulse.Z = 0.0f;
                    }
                    m_limitState = LimitState.AT_LOWER;
                }
                else if (jointAngle >= m_upperAngle)
                {
                    if (m_limitState != LimitState.AT_UPPER)
                    {
                        m_impulse.Z = 0.0f;
                    }
                    m_limitState = LimitState.AT_UPPER;
                }
                else
                {
                    m_limitState = LimitState.INACTIVE;
                    m_impulse.Z = 0.0f;
                }
            }
            else
            {
                m_limitState = LimitState.INACTIVE;
            }

            if (data.Step.WarmStarting)
            {
                Vec2 P = Pool.PopVec2();
                // Scale impulses to support a variable time step.
                m_impulse.X *= data.Step.DtRatio;
                m_impulse.Y *= data.Step.DtRatio;
                m_motorImpulse *= data.Step.DtRatio;

                P.X = m_impulse.X;
                P.Y = m_impulse.Y;

                vA.X -= mA * P.X;
                vA.Y -= mA * P.Y;
                wA -= iA * (Vec2.Cross(m_rA, P) + m_motorImpulse + m_impulse.Z);

                vB.X += mB * P.X;
                vB.Y += mB * P.Y;
                wB += iB * (Vec2.Cross(m_rB, P) + m_motorImpulse + m_impulse.Z);
                Pool.PushVec2(1);
            }
            else
            {
                m_impulse.SetZero();
                m_motorImpulse = 0.0f;
            }

            data.Velocities[m_indexA].V.Set(vA);
            data.Velocities[m_indexA].W = wA;
            data.Velocities[m_indexB].V.Set(vB);
            data.Velocities[m_indexB].W = wB;


            Pool.PushVec2(1);
            Pool.PushRot(2);
        }

        public override void SolveVelocityConstraints(SolverData data)
        {
            Vec2 vA = data.Velocities[m_indexA].V;
            float wA = data.Velocities[m_indexA].W;
            Vec2 vB = data.Velocities[m_indexB].V;
            float wB = data.Velocities[m_indexB].W;

            float mA = m_invMassA, mB = m_invMassB;
            float iA = m_invIA, iB = m_invIB;

            bool fixedRotation = (iA + iB == 0.0f);

            // Solve motor constraint.
            if (m_enableMotor && m_limitState != LimitState.EQUAL && fixedRotation == false)
            {
                float Cdot = wB - wA - m_motorSpeed;
                float impulse = (-m_motorMass) * Cdot;
                float oldImpulse = m_motorImpulse;
                float maxImpulse = data.Step.Dt * m_maxMotorTorque;
                m_motorImpulse = MathUtils.Clamp(m_motorImpulse + impulse, -maxImpulse, maxImpulse);
                impulse = m_motorImpulse - oldImpulse;

                wA -= iA * impulse;
                wB += iB * impulse;
            }
            Vec2 temp = Pool.PopVec2();

            // Solve limit constraint.
            if (m_enableLimit && m_limitState != LimitState.INACTIVE && fixedRotation == false)
            {
                Vec2 Cdot1 = Pool.PopVec2();
                Vec3 Cdot = Pool.PopVec3();

                // Solve point-to-point constraint
                Vec2.CrossToOutUnsafe(wA, m_rA, temp);
                Vec2.CrossToOutUnsafe(wB, m_rB, Cdot1);
                Cdot1.AddLocal(vB).SubLocal(vA).SubLocal(temp);
                float Cdot2 = wB - wA;
                Cdot.Set(Cdot1.X, Cdot1.Y, Cdot2);

                Vec3 impulse = Pool.PopVec3();
                m_mass.Solve33ToOut(Cdot, impulse);
                impulse.NegateLocal();

                if (m_limitState == LimitState.EQUAL)
                {
                    m_impulse.AddLocal(impulse);
                }
                else if (m_limitState == LimitState.AT_LOWER)
                {
                    float newImpulse = m_impulse.Z + impulse.Z;
                    if (newImpulse < 0.0f)
                    {
                        //UPGRADE_NOTE: Final was removed from the declaration of 'rhs '. "ms-help://MS.VSCC.v80/dv_commoner/local/redirect.htm?index='!DefaultContextWindowIndex'&keyword='jlca1003'"
                        Vec2 rhs = Pool.PopVec2();
                        rhs.Set(m_mass.Ez.X, m_mass.Ez.Y).MulLocal(m_impulse.Z).SubLocal(Cdot1);
                        m_mass.Solve22ToOut(rhs, temp);
                        impulse.X = temp.X;
                        impulse.Y = temp.Y;
                        impulse.Z = -m_impulse.Z;
                        m_impulse.X += temp.X;
                        m_impulse.Y += temp.Y;
                        m_impulse.Z = 0.0f;
                        Pool.PushVec2(1);
                    }
                    else
                    {
                        m_impulse.AddLocal(impulse);
                    }
                }
                else if (m_limitState == LimitState.AT_UPPER)
                {
                    float newImpulse = m_impulse.Z + impulse.Z;
                    if (newImpulse > 0.0f)
                    {
                        Vec2 rhs = Pool.PopVec2();
                        rhs.Set(m_mass.Ez.X, m_mass.Ez.Y).MulLocal(m_impulse.Z).SubLocal(Cdot1);
                        m_mass.Solve22ToOut(rhs, temp);
                        impulse.X = temp.X;
                        impulse.Y = temp.Y;
                        impulse.Z = -m_impulse.Z;
                        m_impulse.X += temp.X;
                        m_impulse.Y += temp.Y;
                        m_impulse.Z = 0.0f;
                        Pool.PushVec2(1);
                    }
                    else
                    {
                        m_impulse.AddLocal(impulse);
                    }
                }
                Vec2 P = Pool.PopVec2();

                P.Set(impulse.X, impulse.Y);

                vA.X -= mA * P.X;
                vA.Y -= mA * P.Y;
                wA -= iA * (Vec2.Cross(m_rA, P) + impulse.Z);

                vB.X += mB * P.X;
                vB.Y += mB * P.Y;
                wB += iB * (Vec2.Cross(m_rB, P) + impulse.Z);

                Pool.PushVec2(2);
                Pool.PushVec3(2);
            }
            else
            {

                // Solve point-to-point constraint
                Vec2 Cdot = Pool.PopVec2();
                Vec2 impulse = Pool.PopVec2();

                Vec2.CrossToOutUnsafe(wA, m_rA, temp);
                Vec2.CrossToOutUnsafe(wB, m_rB, Cdot);
                Cdot.AddLocal(vB).SubLocal(vA).SubLocal(temp);
                m_mass.Solve22ToOut(Cdot.NegateLocal(), impulse); // just leave negated

                m_impulse.X += impulse.X;
                m_impulse.Y += impulse.Y;

                vA.X -= mA * impulse.X;
                vA.Y -= mA * impulse.Y;
                wA -= iA * Vec2.Cross(m_rA, impulse);

                vB.X += mB * impulse.X;
                vB.Y += mB * impulse.Y;
                wB += iB * Vec2.Cross(m_rB, impulse);

                Pool.PushVec2(2);
            }

            data.Velocities[m_indexA].V.Set(vA);
            data.Velocities[m_indexA].W = wA;
            data.Velocities[m_indexB].V.Set(vB);
            data.Velocities[m_indexB].W = wB;

            Pool.PushVec2(1);
        }

        public override bool SolvePositionConstraints(SolverData data)
        {
            Rot qA = Pool.PopRot();
            Rot qB = Pool.PopRot();
            Vec2 cA = data.Positions[m_indexA].C;
            float aA = data.Positions[m_indexA].A;
            Vec2 cB = data.Positions[m_indexB].C;
            float aB = data.Positions[m_indexB].A;

            qA.Set(aA);
            qB.Set(aB);

            float angularError = 0.0f;
            float positionError = 0.0f;

            bool fixedRotation = (m_invIA + m_invIB == 0.0f);

            // Solve angular limit constraint.
            if (m_enableLimit && m_limitState != LimitState.INACTIVE && fixedRotation == false)
            {
                float angle = aB - aA - m_referenceAngle;
                float limitImpulse = 0.0f;

                if (m_limitState == LimitState.EQUAL)
                {
                    // Prevent large angular corrections
                    float C = MathUtils.Clamp(angle - m_lowerAngle, -Settings.MAX_ANGULAR_CORRECTION, Settings.MAX_ANGULAR_CORRECTION);
                    limitImpulse = (-m_motorMass) * C;
                    angularError = MathUtils.Abs(C);
                }
                else if (m_limitState == LimitState.AT_LOWER)
                {
                    float C = angle - m_lowerAngle;
                    angularError = -C;

                    // Prevent large angular corrections and allow some slop.
                    C = MathUtils.Clamp(C + Settings.ANGULAR_SLOP, -Settings.MAX_ANGULAR_CORRECTION, 0.0f);
                    limitImpulse = (-m_motorMass) * C;
                }
                else if (m_limitState == LimitState.AT_UPPER)
                {
                    float C = angle - m_upperAngle;
                    angularError = C;

                    // Prevent large angular corrections and allow some slop.
                    C = MathUtils.Clamp(C - Settings.ANGULAR_SLOP, 0.0f, Settings.MAX_ANGULAR_CORRECTION);
                    limitImpulse = (-m_motorMass) * C;
                }

                aA -= m_invIA * limitImpulse;
                aB += m_invIB * limitImpulse;
            }
            // Solve point-to-point constraint.
            {
                qA.Set(aA);
                qB.Set(aB);

                Vec2 rA = Pool.PopVec2();
                Vec2 rB = Pool.PopVec2();
                Vec2 C = Pool.PopVec2();
                Vec2 impulse = Pool.PopVec2();

                Rot.MulToOutUnsafe(qA, C.Set(m_localAnchorA).SubLocal(m_localCenterA), rA);
                Rot.MulToOutUnsafe(qB, C.Set(m_localAnchorB).SubLocal(m_localCenterB), rB);
                C.Set(cB).AddLocal(rB).SubLocal(cA).SubLocal(rA);
                positionError = C.Length();

                float mA = m_invMassA, mB = m_invMassB;
                float iA = m_invIA, iB = m_invIB;

                Mat22 K = Pool.PopMat22();
                K.Ex.X = mA + mB + iA * rA.Y * rA.Y + iB * rB.Y * rB.Y;
                K.Ex.Y = (-iA) * rA.X * rA.Y - iB * rB.X * rB.Y;
                K.Ey.X = K.Ex.Y;
                K.Ey.Y = mA + mB + iA * rA.X * rA.X + iB * rB.X * rB.X;

                K.SolveToOut(C, impulse);
                impulse.NegateLocal();

                cA.X -= mA * impulse.X;
                cA.Y -= mA * impulse.Y;
                aA -= iA * Vec2.Cross(rA, impulse);

                cB.X += mB * impulse.X;
                cB.Y += mB * impulse.Y;
                aB += iB * Vec2.Cross(rB, impulse);

                Pool.PushVec2(4);
                Pool.PushMat22(1);
            }
            data.Positions[m_indexA].C.Set(cA);
            data.Positions[m_indexA].A = aA;
            data.Positions[m_indexB].C.Set(cB);
            data.Positions[m_indexB].A = aB;

            Pool.PushRot(2);

            return positionError <= Settings.LINEAR_SLOP && angularError <= Settings.ANGULAR_SLOP;
        }

        public override void GetAnchorA(Vec2 argOut)
        {
            BodyA.GetWorldPointToOut(m_localAnchorA, argOut);
        }

        public override void GetAnchorB(Vec2 argOut)
        {
            BodyB.GetWorldPointToOut(m_localAnchorB, argOut);
        }

        public override void GetReactionForce(float inv_dt, Vec2 argOut)
        {
            argOut.Set(m_impulse.X, m_impulse.Y).MulLocal(inv_dt);
        }

        public override float GetReactionTorque(float inv_dt)
        {
            return inv_dt * m_impulse.Z;
        }

        public float JointAngle
        {
            get
            {
                Body b1 = BodyA;
                Body b2 = BodyB;
                return b2.Sweep.A - b1.Sweep.A - m_referenceAngle;
            }
        }

        public float JointSpeed
        {
            get
            {
                Body b1 = BodyA;
                Body b2 = BodyB;
                return b2.AngularVelocity - b1.AngularVelocity;
            }
        }

        public bool MotorEnabled
        {
            get
            {
                return m_enableMotor;
            }
        }

        public void enableMotor(bool flag)
        {
            BodyA.Awake = true;
            BodyB.Awake = true;
            m_enableMotor = flag;
        }

        public float getMotorTorque(float inv_dt)
        {
            return m_motorImpulse * inv_dt;
        }

        public float MotorSpeed
        {
            set
            {
                BodyA.Awake = true;
                BodyB.Awake = true;
                m_motorSpeed = value;
            }
        }

        public float MaxMotorTorque
        {
            set
            {
                BodyA.Awake = true;
                BodyB.Awake = true;
                m_maxMotorTorque = value;
            }
        }

        public bool LimitEnabled
        {
            get
            {
                return m_enableLimit;
            }
        }

        public void enableLimit(bool flag)
        {
            if (flag != m_enableLimit)
            {
                BodyA.Awake = true;
                BodyB.Awake = true;
                m_enableLimit = flag;
                m_impulse.Z = 0.0f;
            }
        }

        public float LowerLimit
        {
            get
            {
                return m_lowerAngle;
            }
        }

        public float UpperLimit
        {
            get
            {
                return m_upperAngle;
            }
        }

        public void setLimits(float lower, float upper)
        {
            Debug.Assert(lower <= upper);
            if (lower != m_lowerAngle || upper != m_upperAngle)
            {
                BodyA.Awake = true;
                BodyB.Awake = true;
                m_impulse.Z = 0.0f;
                m_lowerAngle = lower;
                m_upperAngle = upper;
            }
        }
    }
}