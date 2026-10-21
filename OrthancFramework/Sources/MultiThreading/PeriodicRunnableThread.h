/**
 * Stone of Orthanc
 * Copyright (C) 2012-2016 Sebastien Jodogne, Medical Physics
 * Department, University Hospital of Liege, Belgium
 * Copyright (C) 2017-2023 Osimis S.A., Belgium
 * Copyright (C) 2021-2026 Sebastien Jodogne, ICTEAM UCLouvain, Belgium
 *
 * This program is free software: you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this program. If not, see
 * <http://www.gnu.org/licenses/>.
 **/


#pragma once

#include "IRunnable.h"

#include <boost/thread.hpp>


namespace Orthanc
{
  // This is a thread that periodically executes a runnable
  class ORTHANC_PUBLIC PeriodicRunnableThread : public boost::noncopyable
  {
  private:
    enum State
    {
      State_Initialization,
      State_Running,
      State_Done
    };

    static void Worker(PeriodicRunnableThread* that);

    void StopInternal(bool throws);

    boost::mutex                mutex_;
    std::unique_ptr<IRunnable>  runnable_;
    unsigned int                timeResolution_;
    volatile State              state_;
    boost::thread               thread_;

  public:
    PeriodicRunnableThread(IRunnable* runnable /* takes ownership */,
                           unsigned int timeResolution);

    ~PeriodicRunnableThread()
    {
      StopInternal(false /* no exception in destructors */);
    }

    void Start();

    void Stop()
    {
      StopInternal(true);
    }

    IRunnable& GetRunnable() const
    {
      return *runnable_;
    }
  };
}
